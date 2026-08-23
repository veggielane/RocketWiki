using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>Thin GraphQL layer over <see cref="IWatchService"/> — see Mutation.cs's
/// class doc for the shared plumbing (MutationAuthHelper) and why success/denial
/// auditing splits the way it does. Watching requires canView on the page (or any
/// role in the space); unwatching requires nothing beyond owning the Watch row —
/// see IWatchService's own doc for why that asymmetry is deliberate.</summary>
public partial class Mutation
{
    [AuditAction("watch.add")]
    public async Task<WatchPagePayload> WatchPage(
        WatchPageRequest input,
        [Service] IWatchService watchService,
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
            return new WatchPagePayload(null, unauthenticated);
        }

        var result = await watchService.WatchPageAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "watch.add", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new WatchPagePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new WatchPagePayload(WatchView.From(result.Value), null);
    }

    [AuditAction("watch.remove")]
    public async Task<UnwatchPagePayload> UnwatchPage(
        UnwatchPageRequest input,
        [Service] IWatchService watchService,
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
            return new UnwatchPagePayload(null, unauthenticated);
        }

        var result = await watchService.UnwatchPageAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "watch.remove", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new UnwatchPagePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new UnwatchPagePayload(WatchView.From(result.Value), null);
    }

    [AuditAction("watch.add")]
    public async Task<WatchSpacePayload> WatchSpace(
        WatchSpaceRequest input,
        [Service] IWatchService watchService,
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
            return new WatchSpacePayload(null, unauthenticated);
        }

        var result = await watchService.WatchSpaceAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "watch.add", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new WatchSpacePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new WatchSpacePayload(WatchView.From(result.Value), null);
    }

    [AuditAction("watch.remove")]
    public async Task<UnwatchSpacePayload> UnwatchSpace(
        UnwatchSpaceRequest input,
        [Service] IWatchService watchService,
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
            return new UnwatchSpacePayload(null, unauthenticated);
        }

        var result = await watchService.UnwatchSpaceAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "watch.remove", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new UnwatchSpacePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new UnwatchSpacePayload(WatchView.From(result.Value), null);
    }
}

/// <summary>
/// Flat projection of a Watch row rather than the entity itself: the entity carries
/// Page/Space navigations, and exposing those here would create a Page-returning path
/// that bypasses <see cref="PageType"/>'s object-level canView enforcement. Exactly one
/// of <c>pageId</c>/<c>spaceId</c> is set (the row's own xor constraint).
/// </summary>
public sealed record WatchView(Guid Id, Guid? PageId, Guid? SpaceId, DateTime CreatedAtUtc)
{
    public static WatchView From(Watch watch) => new(watch.Id, watch.PageId, watch.SpaceId, watch.CreatedAtUtc);
}

public sealed record WatchPagePayload(WatchView? Watch, PageMutationErrorView? Error);
public sealed record UnwatchPagePayload(WatchView? Watch, PageMutationErrorView? Error);
public sealed record WatchSpacePayload(WatchView? Watch, PageMutationErrorView? Error);
public sealed record UnwatchSpacePayload(WatchView? Watch, PageMutationErrorView? Error);
