using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Thin GraphQL layer over <see cref="ISpaceService"/>, same shape as Mutation.cs's
/// Page mutations. <see cref="IInstanceRoleAccessor"/> is resolved here and passed
/// through as the caller-resolved <c>isInstanceAdmin</c> bool every method on
/// <c>ISpaceService</c> takes — the service enforces the actual gate (design.md
/// §6.5.1: create is instance-admin only; rename/archive/restore accept instance-admin
/// OR that space's own space-admin), this layer only resolves and forwards it.
///
/// <c>createSpace</c> takes <c>initialGrant</c> as a second, separate input alongside
/// <c>CreateSpaceRequest</c> (design.md §6.5.1: creation is atomic with its first
/// grant, a required input with no default) — landed in <c>ISpaceService</c> mid-flight
/// during this same round, exactly as flagged.
/// </summary>
public partial class Mutation
{
    [AuditAction("space.create")]
    public async Task<CreateSpacePayload> CreateSpace(
        CreateSpaceRequest input,
        InitialSpaceGrant initialGrant,
        [Service] ISpaceService spaceService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new CreateSpacePayload(null, unauthenticated);
        }

        var result = await spaceService.CreateAsync(
            input, initialGrant, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "space.create", result.Error, AuditSubjectType.Space, subjectId: null, cancellationToken);
            return new CreateSpacePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new CreateSpacePayload(result.Value, null);
    }

    [AuditAction("space.rename")]
    public async Task<RenameSpacePayload> RenameSpace(
        RenameSpaceRequest input,
        [Service] ISpaceService spaceService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new RenameSpacePayload(null, unauthenticated);
        }

        var result = await spaceService.RenameAsync(
            input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "space.rename", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new RenameSpacePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new RenameSpacePayload(result.Value, null);
    }

    /// <summary>
    /// A dedicated mutation rather than another field on <c>renameSpace</c>, matching
    /// <c>setPageMarking</c>/<c>setPageProperty</c>: a homepage is not a rename, and
    /// folding it in would mean every rename had to restate the current homepage or
    /// silently clear it — exactly the ambiguity a single-purpose setter removes.
    /// A null <c>pageId</c> clears the homepage (see SetSpaceHomepageRequest).
    /// </summary>
    [AuditAction("space.homepage.set")]
    public async Task<SetSpaceHomepagePayload> SetSpaceHomepage(
        SetSpaceHomepageRequest input,
        [Service] ISpaceService spaceService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new SetSpaceHomepagePayload(null, unauthenticated);
        }

        var result = await spaceService.SetHomepageAsync(
            input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "space.homepage.set", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new SetSpaceHomepagePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new SetSpaceHomepagePayload(result.Value, null);
    }

    /// <summary>
    /// design.md §12's low-side export switch — see <c>ISpaceService.SetExportedAsync</c>
    /// for the gate and why it is instance-admin-only rather than sharing
    /// archive/rename's "or space admin" arm.
    ///
    /// <para>The declared <c>[AuditAction]</c> is <c>space.export.enabled</c>, matching
    /// how <c>setPageMarking</c> declares <c>page.marking.set</c> for a mutation that
    /// emits one of two action names: the coverage guard and the denial rows need ONE
    /// declared name, and the enabling direction is the one a reviewer is looking for, so
    /// it is the honest choice for a refusal too. The success row's action is derived
    /// from the event in DomainEventAuditMapper.</para>
    /// </summary>
    [AuditAction("space.export.enabled")]
    public async Task<SetSpaceExportedPayload> SetSpaceExported(
        SetSpaceExportedRequest input,
        [Service] ISpaceService spaceService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new SetSpaceExportedPayload(null, unauthenticated);
        }

        var result = await spaceService.SetExportedAsync(
            input, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "space.export.enabled", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new SetSpaceExportedPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new SetSpaceExportedPayload(result.Value, null);
    }

    [AuditAction("space.archive")]
    public async Task<ArchiveSpacePayload> ArchiveSpace(
        ArchiveSpaceRequest input,
        [Service] ISpaceService spaceService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        [Service] IPresenceRuleChangeNotifier ruleChangeNotifier,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new ArchiveSpacePayload(null, unauthenticated);
        }

        var result = await spaceService.ArchiveAsync(
            input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "space.archive", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new ArchiveSpacePayload(null, PageMutationErrorView.From(result.Error));
        }

        // design.md §8: an access change has to reach live sessions, not just the
        // database. An archived space is hidden and read-only, so anyone
        // still joined to one of its pages is holding a session the space no longer
        // offers.
        await ruleChangeNotifier.NotifyRulesChangedAsync(cancellationToken);

        return new ArchiveSpacePayload(result.Value, null);
    }

    [AuditAction("space.restore")]
    public async Task<RestoreSpacePayload> RestoreSpace(
        RestoreSpaceRequest input,
        [Service] ISpaceService spaceService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new RestoreSpacePayload(null, unauthenticated);
        }

        var result = await spaceService.RestoreAsync(
            input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "space.restore", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new RestoreSpacePayload(null, PageMutationErrorView.From(result.Error));
        }

        return new RestoreSpacePayload(result.Value, null);
    }
}

public sealed record CreateSpacePayload(Space? Space, PageMutationErrorView? Error);
public sealed record RenameSpacePayload(Space? Space, PageMutationErrorView? Error);
public sealed record SetSpaceHomepagePayload(Space? Space, PageMutationErrorView? Error);
public sealed record SetSpaceExportedPayload(Space? Space, PageMutationErrorView? Error);
public sealed record ArchiveSpacePayload(Space? Space, PageMutationErrorView? Error);
public sealed record RestoreSpacePayload(Space? Space, PageMutationErrorView? Error);
