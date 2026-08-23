using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Thin GraphQL layer over <see cref="IPageService"/> — no domain logic lives here.
/// design.md §7: mutations get their *successful* audit row from the domain-event
/// pipeline inside the service itself (RaiseDomainEvent, same transaction as the
/// change); a *denied* mutation never reaches RaiseDomainEvent at all, so recording
/// that denial (with its reason - design.md §7) is this layer's job, done explicitly
/// per mutation below rather than through AuditFieldMiddleware (see its own doc for
/// why mutations are excluded from that generic dispatch). Shared plumbing
/// (Principal/actingUserId/AuditContext resolution, denial auditing) lives in
/// <see cref="MutationAuthHelper"/> — every *Service in RocketWiki.Core.Services
/// follows the same shape, so Comment/Label/Attachment mutations reuse it too.
/// </summary>
public partial class Mutation
{
    [AuditAction("page.create")]
    public async Task<CreatePagePayload> CreatePage(
        CreatePageRequest input,
        [Service] IPageService pageService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] INotificationDispatcher notificationDispatcher,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new CreatePagePayload(null, unauthenticated);
        }

        var result = await pageService.CreatePageAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "page.create", result.Error, AuditSubjectType.Page, subjectId: null, cancellationToken);
            return new CreatePagePayload(null, PageMutationErrorView.From(result.Error));
        }

        // design.md §8: a brand-new page notifies its space's watchers, and anyone
        // mentioned in the initial content (revision 1, so every mention is "new").
        await notificationDispatcher.NotifyPageChangedAsync(result.Value.Id, actingUserId.Value, NotificationType.PageUpdated, cancellationToken);

        return new CreatePagePayload(result.Value, null);
    }

    [AuditAction("page.edit")]
    public async Task<UpdatePageContentPayload> UpdatePageContent(
        UpdatePageContentRequest input,
        [Service] IPageService pageService,
        [Service] IEditSessionRegistry editSessions,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] INotificationDispatcher notificationDispatcher,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new UpdatePageContentPayload(null, unauthenticated);
        }

        // Co-editing attribution (design.md §8/§7): contributor ids come EXCLUSIVELY
        // from the server's own edit-session registry, resolved by the acting user's
        // live membership in this page's session - never from the request. There is
        // deliberately no `contributors` input field for a client to forge; a
        // non-member's save (content produced outside the session) attaches nothing.
        var sessionContributors = editSessions.SnapshotContributorsForSave(input.PageId, actingUserId!.Value);

        var result = await pageService.UpdatePageContentAsync(
            input, principal!, actingUserId.Value, auditContext!, sessionContributors?.UserIds, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "page.edit", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new UpdatePageContentPayload(null, PageMutationErrorView.From(result.Error));
        }

        if (sessionContributors is not null)
        {
            // Drain only up to the snapshot's sequence: keystrokes that landed while
            // this save was in flight stay marked for the next revision. Also advances
            // the session's base revision so late joiners save against the new number.
            editSessions.OnSaved(input.PageId, sessionContributors.MaxSequence, result.Value.CurrentRevisionNumber);
        }

        // design.md §8: watchers of this page/space - per-recipient canView at send
        // time for connected recipients, deferred fetch-time-gated rows for offline
        // ones (see NotificationDispatcher's own doc).
        await notificationDispatcher.NotifyPageChangedAsync(result.Value.Id, actingUserId.Value, NotificationType.PageUpdated, cancellationToken);

        return new UpdatePageContentPayload(result.Value, null);
    }

    [AuditAction("page.move")]
    public async Task<MovePagePayload> MovePage(
        MovePageRequest input,
        [Service] IPageService pageService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new MovePagePayload(null, unauthenticated);
        }

        var result = await pageService.MovePageAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "page.move", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new MovePagePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new MovePagePayload(result.Value, null);
    }

    [AuditAction("page.delete")]
    public async Task<DeletePagePayload> DeletePage(
        DeletePageRequest input,
        [Service] IPageService pageService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new DeletePagePayload(null, unauthenticated);
        }

        var result = await pageService.DeletePageAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "page.delete", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new DeletePagePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new DeletePagePayload(result.Value, null);
    }

    [AuditAction("page.restore")]
    public async Task<RestorePagePayload> RestorePage(
        RestorePageRequest input,
        [Service] IPageService pageService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new RestorePagePayload(null, unauthenticated);
        }

        var result = await pageService.RestorePageAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "page.restore", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new RestorePagePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new RestorePagePayload(result.Value, null);
    }

    [AuditAction("page.restoreRevision")]
    public async Task<RestoreRevisionPayload> RestoreRevision(
        RestoreRevisionRequest input,
        [Service] IPageService pageService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new RestoreRevisionPayload(null, unauthenticated);
        }

        var result = await pageService.RestoreRevisionAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "page.restoreRevision", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new RestoreRevisionPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new RestoreRevisionPayload(result.Value, null);
    }
}

public sealed record CreatePagePayload(Page? Page, PageMutationErrorView? Error);
public sealed record UpdatePageContentPayload(Page? Page, PageMutationErrorView? Error);
public sealed record MovePagePayload(Page? Page, PageMutationErrorView? Error);
public sealed record DeletePagePayload(PageDeleteSummary? Summary, PageMutationErrorView? Error);
public sealed record RestorePagePayload(PageRestoreSummary? Summary, PageMutationErrorView? Error);
public sealed record RestoreRevisionPayload(Page? Page, PageMutationErrorView? Error);
