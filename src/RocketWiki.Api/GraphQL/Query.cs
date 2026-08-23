using System.Security.Claims;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Root query fields (design.md §8); see Query.Spaces.cs, Query.AuditEvents.cs,
/// Query.Search.cs, and Query.Labels.cs for the rest of this partial class.
/// </summary>
public partial class Query
{
    /// <summary>
    /// design.md §6.7: null for a page that doesn't exist OR one the caller can't
    /// view — indistinguishable to the caller, but not to the audit log: the service's
    /// internal ReadResult distinguishes them exactly long enough for the Denied case
    /// to be recorded with its failing restriction (§7), then both collapse to the
    /// same null right here. An anonymous request (no Principal) is treated
    /// identically, without even calling the service (design.md: no anonymous wikis,
    /// so there is nothing an anonymous Principal could pass) — and without an audit
    /// row, since no access decision was made and DbAuditSink refuses rows with no
    /// resolvable acting user anyway.
    /// </summary>
    [AuditAction("page.view")]
    [UseAuditDispatch]
    public async Task<Page?> Page(
        Guid id,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        var result = await readService.GetPageAsync(id, principal, cancellationToken);
        if (result is ReadResult<Page>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "page.view", AuditSubjectType.Page, id, denied.Reason, cancellationToken);
        }

        return result.ValueOrNull();
    }

    /// <summary>
    /// design.md §6.7/§8: the space's page tree, already pruned to what the caller
    /// can view — a restricted subtree is simply absent, not flagged. Stands in for
    /// design.md's `Space.tree` field until a space read service exists to resolve
    /// `Space` itself with the same care <see cref="PageType"/> gives `Page`.
    /// </summary>
    [AuditAction("space.browse")]
    [UseAuditDispatch]
    public async Task<IReadOnlyList<PageTreeNode>> PageTree(
        Guid spaceId,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return [];
        }

        // Found may itself carry an empty list (everything pruned), so Denied and
        // NotFound collapsing to [] leaves all three caller-indistinguishable
        // (design.md §6.7) - only the audit log learns which one happened (§7).
        // DbAuditSink suppresses AuditFieldMiddleware's would-be Success row for a
        // subject already recorded as Denied this request, so a refused browse never
        // also claims success.
        var result = await readService.GetPageTreeAsync(spaceId, principal, cancellationToken);
        if (result is ReadResult<IReadOnlyList<PageTreeNode>>.Denied denied)
        {
            await ReadDenialAudit.RecordAsync(
                auditSink, "space.browse", AuditSubjectType.Space, spaceId, denied.Reason, cancellationToken);
        }

        return result.ValueOrNull() ?? [];
    }

    /// <summary>
    /// Returns the caller's identity as seen by the API, built directly from the
    /// validated token claims — never from a local user mirror (design.md §6.1,
    /// §11). This is a read of "who am I", not a domain read, so it intentionally
    /// carries no audit event; every real root field will (design.md §7/§8).
    /// </summary>
    [NoAudit("Echoes claims already on the caller's own validated token; not a read of wiki content (design.md §7).")]
    public CurrentUser Me(ClaimsPrincipal claimsPrincipal)
    {
        if (claimsPrincipal.Identity?.IsAuthenticated != true)
        {
            return CurrentUser.Anonymous;
        }

        var userId = claimsPrincipal.FindFirst("sub")?.Value
            ?? claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = claimsPrincipal.FindFirst("email")?.Value
            ?? claimsPrincipal.FindFirst(ClaimTypes.Email)?.Value;
        var name = claimsPrincipal.FindFirst("name")?.Value
            ?? claimsPrincipal.FindFirst(ClaimTypes.Name)?.Value;

        // The "groups" claim is expected from a Keycloak protocol mapper
        // (design.md §11.5); this is the raw claim only — the ABAC Principal
        // (groups + registered attributes) is built by the rule engine, not here.
        var groups = claimsPrincipal.FindAll("groups").Select(c => c.Value).ToArray();

        return new CurrentUser(userId, email, name, groups, IsAuthenticated: true);
    }
}

/// <summary>
/// Claims-derived view of the caller. Not the domain `User` entity (design.md
/// §5) — that is JIT-provisioned from these same claims elsewhere (§11.3) and
/// belongs to RocketWiki.Core/RocketWiki.Data, not the API layer.
/// </summary>
public sealed record CurrentUser(
    string? Id,
    string? Email,
    string? Name,
    IReadOnlyList<string> Groups,
    bool IsAuthenticated)
{
    public static readonly CurrentUser Anonymous = new(null, null, null, [], false);
}
