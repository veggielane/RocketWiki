using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// design.md §8's schema sketch names two mutations here, <c>setSpaceGrants</c> and
/// <c>setPageRestrictions</c>; <see cref="IAccessRuleService"/> is explicitly "one
/// CRUD-shaped service [that] covers both kinds" (its own doc comment), since a
/// SpaceGrant and a PageRestriction are both just <c>AccessRule</c> rows distinguished
/// by <c>Kind</c>. Mirroring that 1:1 as a plain create/update/delete trio — rather
/// than inventing bulk "replace the whole grant set" diffing semantics the interface
/// doesn't support — is a deliberate simplification, the same spirit as
/// <see cref="PageMutationErrorView"/>'s flattening: every fact the sketch's two names
/// would carry is still reachable, just through generic verbs instead of two
/// resource-specific ones.
///
/// design.md §6.5.2: rule management accepts instance-admin OR that space's own
/// space-admin — <see cref="IInstanceRoleAccessor"/> is resolved here and forwarded
/// as the caller-resolved <c>isInstanceAdmin</c> bool, landed in
/// <c>IAccessRuleService</c> mid-flight during this same round, exactly as flagged.
/// </summary>
public partial class Mutation
{
    [AuditAction("permission.change")]
    public async Task<CreateAccessRulePayload> CreateAccessRule(
        CreateAccessRuleRequest input,
        [Service] IAccessRuleService accessRuleService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IPresenceRuleChangeNotifier ruleChangeNotifier,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new CreateAccessRulePayload(null, unauthenticated);
        }

        var result = await accessRuleService.CreateAsync(input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "permission.change", result.Error, AuditSubjectType.Rule, subjectId: null, cancellationToken);
            return new CreateAccessRulePayload(null, PageMutationErrorView.From(result.Error));
        }

        await ruleChangeNotifier.NotifyRulesChangedAsync(cancellationToken);
        return new CreateAccessRulePayload(result.Value, null);
    }

    [AuditAction("permission.change")]
    public async Task<UpdateAccessRulePayload> UpdateAccessRule(
        UpdateAccessRuleRequest input,
        [Service] IAccessRuleService accessRuleService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IPresenceRuleChangeNotifier ruleChangeNotifier,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new UpdateAccessRulePayload(null, unauthenticated);
        }

        var result = await accessRuleService.UpdateAsync(input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "permission.change", result.Error, AuditSubjectType.Rule, input.AccessRuleId, cancellationToken);
            return new UpdateAccessRulePayload(null, PageMutationErrorView.From(result.Error));
        }

        await ruleChangeNotifier.NotifyRulesChangedAsync(cancellationToken);
        return new UpdateAccessRulePayload(result.Value, null);
    }

    [AuditAction("permission.change")]
    public async Task<DeleteAccessRulePayload> DeleteAccessRule(
        DeleteAccessRuleRequest input,
        [Service] IAccessRuleService accessRuleService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IPresenceRuleChangeNotifier ruleChangeNotifier,
        CancellationToken cancellationToken)
    {
        var (principal, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new DeleteAccessRulePayload(null, unauthenticated);
        }

        var result = await accessRuleService.DeleteAsync(input, principal!, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "permission.change", result.Error, AuditSubjectType.Rule, input.AccessRuleId, cancellationToken);
            return new DeleteAccessRulePayload(null, PageMutationErrorView.From(result.Error));
        }

        await ruleChangeNotifier.NotifyRulesChangedAsync(cancellationToken);
        return new DeleteAccessRulePayload(result.Value, null);
    }
}

public sealed record CreateAccessRulePayload(AccessRule? Rule, PageMutationErrorView? Error);
public sealed record UpdateAccessRulePayload(AccessRule? Rule, PageMutationErrorView? Error);
public sealed record DeleteAccessRulePayload(Guid? DeletedRuleId, PageMutationErrorView? Error);
