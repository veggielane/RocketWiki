using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The marking on an entry, as a caller states it. Level and country set together,
/// never separately: a marking is one value, and a partial update would let a caller
/// change the level without ever saying what caveat they meant — the same reason
/// `setPageMarking` replaces the whole thing (§21.6).
///
/// <para>Omitting this whole input on create means "inherit the page's", which is the
/// ordinary case. On update it means "leave the marking alone" — an entry has no unmarked
/// state to clear to.</para>
/// </summary>
public sealed record PageEntryMarkingInput(ClassificationLevel Level, string[]? EyesOnly, string? Prefix)
{
    public ProtectiveMarking ToMarking() => ProtectiveMarking.Create(Level, EyesOnly ?? [], selectors: null, Prefix);
}

public sealed record CreatePageEntryInput(Guid PageId, string Collection, string Data, PageEntryMarkingInput? Marking);

public sealed record UpdatePageEntryInput(Guid EntryId, int ExpectedVersion, string Data, PageEntryMarkingInput? Marking);

public sealed record DeletePageEntryInput(Guid EntryId, int ExpectedVersion);

public sealed record CreatePageEntryPayload(PageEntryView? Entry, PageMutationErrorView? Error);

public sealed record UpdatePageEntryPayload(PageEntryView? Entry, PageMutationErrorView? Error);

public sealed record DeletePageEntryPayload(PageEntryView? Entry, PageMutationErrorView? Error);

/// <summary>
/// Thin GraphQL layer over <see cref="IPageEntryService"/>
/// (docs/ENTRIES-AND-FORMS-PLAN.md) — see Mutation.cs's class doc for the shared
/// plumbing. Denial audits use the Page as subject, following
/// DomainEventAuditMapper's convention for the matching success events: AuditSubjectType
/// is a closed list with no member for an entry, and the page is the meaningful "what was
/// touched" in any case.
/// </summary>
public partial class Mutation
{
    [AuditAction("page.entry.create")]
    public async Task<CreatePageEntryPayload> CreatePageEntry(
        CreatePageEntryInput input,
        [Service] IPageEntryService entryService,
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
            return new CreatePageEntryPayload(null, unauthenticated);
        }

        var result = await entryService.CreateAsync(
            new CreatePageEntryRequest(input.PageId, input.Collection, input.Data, input.Marking?.ToMarking()),
            principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.entry.create", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new CreatePageEntryPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new CreatePageEntryPayload(result.Value, null);
    }

    [AuditAction("page.entry.update")]
    public async Task<UpdatePageEntryPayload> UpdatePageEntry(
        UpdatePageEntryInput input,
        [Service] IPageEntryService entryService,
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
            return new UpdatePageEntryPayload(null, unauthenticated);
        }

        var result = await entryService.UpdateAsync(
            new UpdatePageEntryRequest(input.EntryId, input.ExpectedVersion, input.Data, input.Marking?.ToMarking()),
            principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            // Subject TYPE is Page, but the id is left null: naming it would mean reading
            // the entry to find its page, and the caller may be someone the gate has just
            // refused. An audit row that says "a page entry write was denied" without
            // saying which is the honest record here.
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.entry.update", result.Error, AuditSubjectType.Page, null, cancellationToken);
            return new UpdatePageEntryPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new UpdatePageEntryPayload(result.Value, null);
    }

    [AuditAction("page.entry.delete")]
    public async Task<DeletePageEntryPayload> DeletePageEntry(
        DeletePageEntryInput input,
        [Service] IPageEntryService entryService,
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
            return new DeletePageEntryPayload(null, unauthenticated);
        }

        var result = await entryService.DeleteAsync(
            new DeletePageEntryRequest(input.EntryId, input.ExpectedVersion),
            principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            // Same reasoning as update: the type is known, the id is not obtainable
            // without a read the gate refused.
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.entry.delete", result.Error, AuditSubjectType.Page, null, cancellationToken);
            return new DeletePageEntryPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new DeletePageEntryPayload(result.Value, null);
    }
}
