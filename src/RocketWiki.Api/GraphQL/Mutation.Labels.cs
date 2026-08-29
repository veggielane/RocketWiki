using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>Thin GraphQL layer over <see cref="ILabelService"/> - see Mutation.cs's
/// class doc for the shared plumbing. Subject type for denial audits follows
/// DomainEventAuditMapper's own convention for the matching success events: label
/// creation against the Space, attach/detach against the Page whose label set
/// changed.</summary>
public partial class Mutation
{
    [AuditAction("label.create")]
    public async Task<CreateLabelPayload> CreateLabel(
        CreateLabelRequest input,
        [Service] ILabelService labelService,
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
            return new CreateLabelPayload(null, unauthenticated);
        }

        var result = await labelService.CreateLabelAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "label.create", result.Error, AuditSubjectType.Space, input.SpaceId, cancellationToken);
            return new CreateLabelPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new CreateLabelPayload(result.Value, null);
    }

    [AuditAction("label.attach")]
    public async Task<AttachLabelPayload> AttachLabel(
        AttachLabelRequest input,
        [Service] ILabelService labelService,
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
            return new AttachLabelPayload(null, unauthenticated);
        }

        var result = await labelService.AttachLabelAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "label.attach", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new AttachLabelPayload(null, PageMutationErrorView.From(result.Error));
        }

        return new AttachLabelPayload(result.Value, null);
    }

    [AuditAction("label.detach")]
    public async Task<DetachLabelPayload> DetachLabel(
        DetachLabelRequest input,
        [Service] ILabelService labelService,
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
            return new DetachLabelPayload(null, unauthenticated);
        }

        var result = await labelService.DetachLabelAsync(input, principal!, actingUserId!.Value, auditContext!, cancellationToken);
        if (!result.IsSuccess)
        {
            await MutationAuthHelper.AuditDenialIfApplicableAsync(auditSink, "label.detach", result.Error, AuditSubjectType.Page, input.PageId, cancellationToken);
            return new DetachLabelPayload(null, PageMutationErrorView.From(result.Error));
        }

        // The association that was removed, not a bare id.
        //
        // A payload of scalars carries no __typename, and urql's document cache
        // invalidates on the typenames a mutation RETURNS — so this mutation could
        // not invalidate anything, and removing a label left a stale chip in every
        // cached tree until something else happened to refetch. No additionalTypenames
        // list could fix that from the query side: there was nothing to match.
        //
        // Symmetric with attachLabel, which already returns the PageLabel it created.
        // The row is gone by now; this describes what was removed, which is exactly
        // what a cache needs to know.
        return new DetachLabelPayload(new PageLabel { PageId = input.PageId, LabelId = result.Value }, null);
    }
}

public sealed record CreateLabelPayload(Label? Label, PageMutationErrorView? Error);
public sealed record AttachLabelPayload(PageLabel? PageLabel, PageMutationErrorView? Error);
public sealed record DetachLabelPayload(PageLabel? DetachedLabel, PageMutationErrorView? Error);
