using System.Text.Json;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Thin GraphQL layer over <see cref="IPagePropertyService"/> (design.md §20) — see
/// Mutation.cs's class doc for the shared plumbing. Subject type for denial audits
/// follows DomainEventAuditMapper's own convention for the matching success events:
/// value changes against the Page whose metadata changed, registry changes against
/// nothing at all (no AuditSubjectType fits instance-local vocabulary), with the key
/// named in the details — the same shape CustomEmojiEndpoints uses for its admin gate.
/// </summary>
public partial class Mutation
{
    [AuditAction("page.property.set")]
    public async Task<SetPagePropertyPayload> SetPageProperty(
        SetPagePropertyRequest input,
        [Service] IPagePropertyService propertyService,
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
            return new SetPagePropertyPayload(null, unauthenticated);
        }

        var result = await propertyService.SetAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.property.set", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new SetPagePropertyPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new SetPagePropertyPayload(result.Value, null);
    }

    [AuditAction("page.property.remove")]
    public async Task<RemovePagePropertyPayload> RemovePageProperty(
        RemovePagePropertyRequest input,
        [Service] IPagePropertyService propertyService,
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
            return new RemovePagePropertyPayload(null, unauthenticated);
        }

        var result = await propertyService.RemoveAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(
                auditSink, "page.property.remove", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new RemovePagePropertyPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new RemovePagePropertyPayload(result.Value, null);
    }

    [AuditAction("property_key.create")]
    public async Task<CreatePagePropertyKeyPayload> CreatePagePropertyKey(
        CreatePagePropertyKeyRequest input,
        [Service] IPagePropertyService propertyService,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new CreatePagePropertyKeyPayload(null, unauthenticated);
        }

        var result = await propertyService.CreateKeyAsync(
            input, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await AuditRegistryDenialIfApplicableAsync(auditSink, "property_key.create", result.Error, input.Key, cancellationToken);
            return new CreatePagePropertyKeyPayload(null, PageMutationErrorView.From(result.Error));
        }

        var key = result.Value;
        return new CreatePagePropertyKeyPayload(
            new PagePropertyKeyRef(key.Id, key.Key, key.Description, key.SortOrder), null);
    }

    [AuditAction("property_key.delete")]
    public async Task<DeletePagePropertyKeyPayload> DeletePagePropertyKey(
        Guid keyId,
        [Service] IPagePropertyService propertyService,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new DeletePagePropertyKeyPayload(null, unauthenticated);
        }

        var result = await propertyService.DeleteKeyAsync(
            keyId, instanceRoleAccessor.IsInstanceAdmin, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await AuditRegistryDenialIfApplicableAsync(auditSink, "property_key.delete", result.Error, keyId.ToString(), cancellationToken);
            return new DeletePagePropertyKeyPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new DeletePagePropertyKeyPayload(result.Value, null);
    }

    /// <summary>
    /// The registry equivalent of <see cref="MutationAuthHelper.AuditDenialIfApplicableAsync"/>,
    /// which cannot be used here: it requires an <see cref="AuditSubjectType"/>, and the
    /// registry has none (§7's subject list is wiki-content shapes). Same rule about
    /// WHICH failures earn a row — only permission-shaped ones; a duplicate key or a
    /// key-in-use refusal is an ordinary outcome, not an access refusal. The key
    /// doubles as the DedupKey so two different keys refused in one request both get
    /// their row (see AuditRecord.DedupKey).
    /// </summary>
    private static Task AuditRegistryDenialIfApplicableAsync(
        IAuditSink sink, string action, PageMutationError error, string key, CancellationToken cancellationToken)
    {
        if (error is not ForbiddenError forbidden)
        {
            return Task.CompletedTask;
        }

        return sink.RecordAsync(
            new AuditRecord(
                action, AuditOutcome.Denied,
                DetailsJson: JsonSerializer.Serialize(new { reason = forbidden.Reason, key }),
                DedupKey: key),
            cancellationToken);
    }
}

public sealed record SetPagePropertyPayload(PagePropertyValue? Property, PageMutationErrorView? Error);
public sealed record RemovePagePropertyPayload(Guid? RemovedPropertyKeyId, PageMutationErrorView? Error);
public sealed record CreatePagePropertyKeyPayload(PagePropertyKeyRef? PropertyKey, PageMutationErrorView? Error);
public sealed record DeletePagePropertyKeyPayload(Guid? DeletedPropertyKeyId, PageMutationErrorView? Error);
