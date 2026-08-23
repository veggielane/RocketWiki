using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The GitLab Settings surface (design.md GitLab integration section): store or
/// clear the caller's own GitLab personal access token. The token goes in and
/// never comes back out — no query, payload, or error anywhere in the schema
/// returns stored token material; the only readable fact is
/// <c>gitlabStatus.viewerHasToken</c>. Success audit rows
/// (<c>settings.gitlab_token.set</c>/<c>.cleared</c>) come from the domain-event
/// pipeline in the same transaction as the row change, and carry no token
/// material — the domain events are structurally unable to (GitLabDomainEvents).
///
/// No denial-audit call here, deliberately: the only failure these mutations can
/// produce is a <see cref="ValidationError"/>, which §7's outcome vocabulary
/// excludes (no access decision was made) — same rule MutationAuthHelper applies
/// everywhere else.
/// </summary>
public partial class Mutation
{
    [AuditAction("settings.gitlab_token.set")]
    public async Task<SetGitLabTokenPayload> SetGitLabToken(
        SetGitLabTokenRequest input,
        [Service] IGitLabCredentialService credentialService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new SetGitLabTokenPayload(null, unauthenticated);
        }

        var result = await credentialService.SetTokenAsync(input, actingUserId!.Value, auditContext!, cancellationToken);
        return result.IsSuccess
            ? new SetGitLabTokenPayload(result.Value, null)
            : new SetGitLabTokenPayload(null, PageMutationErrorView.From(result.Error));
    }

    [AuditAction("settings.gitlab_token.cleared")]
    public async Task<ClearGitLabTokenPayload> ClearGitLabToken(
        [Service] IGitLabCredentialService credentialService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IActingUserAccessor actingUserAccessor,
        [Service] ICurrentAuditContextAccessor auditContextAccessor,
        CancellationToken cancellationToken)
    {
        var (_, actingUserId, auditContext, unauthenticated) =
            MutationAuthHelper.Authenticate(principalAccessor, actingUserAccessor, auditContextAccessor);
        if (unauthenticated is not null)
        {
            return new ClearGitLabTokenPayload(null, unauthenticated);
        }

        var result = await credentialService.ClearTokenAsync(actingUserId!.Value, auditContext!, cancellationToken);
        return result.IsSuccess
            ? new ClearGitLabTokenPayload(result.Value, null)
            : new ClearGitLabTokenPayload(null, PageMutationErrorView.From(result.Error));
    }
}

public sealed record SetGitLabTokenPayload(GitLabTokenState? TokenState, PageMutationErrorView? Error);
public sealed record ClearGitLabTokenPayload(GitLabTokenState? TokenState, PageMutationErrorView? Error);
