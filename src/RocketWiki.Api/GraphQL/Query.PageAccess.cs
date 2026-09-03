using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using CorePageAccess = RocketWiki.Core.Services.PageAccess;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The disclosed form of a page read (design.md §6.7 / §21.8): exactly one of
/// <see cref="Page"/> and <see cref="Denial"/> is non-null. The result itself is null
/// only for a page that does not exist — a denied page is a placeholder, not an absence,
/// which is the whole difference from <c>page(id)</c>.
/// </summary>
public sealed record PageAccessResult(Page? Page, AccessDenialView? Denial);

public partial class Query
{
    /// <summary>
    /// The page at this id, or a <c>(protected)</c> placeholder saying why it cannot be
    /// shown (design.md §6.7 / §21.8). Null only when no such live page exists.
    ///
    /// <para><b>Additive beside <see cref="Page"/>, which is unchanged.</b> The plain read
    /// stays null for a denied page exactly as for a missing one, and its byte-identical
    /// tests are kept as the pin that it stays leak-free; this field is the one surface
    /// where a denial is disclosed, so there is exactly one place to analyse what a
    /// denied caller learns. The SPA's page routes read this; anything that only wants
    /// content keeps reading <c>page</c>.</para>
    ///
    /// <para><b>Audit (§7): one request, one <c>page.view</c> row.</b> A denial is
    /// recorded here with the calculator's deterministic reason — the first failing gate
    /// in ladder order, the same token <c>page(id)</c> would write — and the field
    /// middleware records nothing for a placeholder (a placeholder is not a successful
    /// read); a viewable page gets its Success row from the middleware as every other
    /// Page-returning root field does. The structured reason list is not written to the
    /// audit table: what a denied caller is told is a fixed policy, not a per-request
    /// fact, and the token already names the gate.</para>
    ///
    /// <para>An anonymous request gets null without calling the service, as
    /// <see cref="Page"/> does: no principal, no decision, no row.</para>
    /// </summary>
    [AuditAction("page.view")]
    [UseAuditDispatch]
    public async Task<PageAccessResult?> PageAccess(
        Guid id,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        [Service] SelectorCatalog catalog,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        return await ResolvePageAccessAsync(id, principal, readService, auditSink, catalog, cancellationToken);
    }

    /// <summary>
    /// The page addressed by <c>/spaces/{spaceKey}/{slug}</c>, with the same placeholder
    /// contract as <see cref="PageAccess"/>. Resolves the slug to an id and then takes the
    /// identical path — the gate and the audit are not reimplemented.
    ///
    /// <para>Stated because it was weighed: a denied page in a space the caller cannot
    /// enter still answers with the no-space-access placeholder here, so
    /// <c>(spaceKey, slug)</c> confirms that a page exists at that address. That is the
    /// confirmed "space denial only" disclosure (§21.8) applied to the slug route, and
    /// what it withholds is everything else — no marking, no title, no id.</para>
    /// </summary>
    [AuditAction("page.view")]
    [UseAuditDispatch]
    public async Task<PageAccessResult?> PageAccessBySlug(
        string spaceKey,
        string slug,
        [Service] IPageReadService readService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IAuditSink auditSink,
        [Service] SelectorCatalog catalog,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null)
        {
            return null;
        }

        var pageId = await readService.FindPageIdBySlugAsync(spaceKey, slug, cancellationToken);
        if (pageId is null)
        {
            return null;
        }

        return await ResolvePageAccessAsync(pageId.Value, principal, readService, auditSink, catalog, cancellationToken);
    }

    private static async Task<PageAccessResult?> ResolvePageAccessAsync(
        Guid pageId,
        Principal principal,
        IPageReadService readService,
        IAuditSink auditSink,
        SelectorCatalog catalog,
        CancellationToken cancellationToken)
    {
        var result = await readService.GetPageAccessAsync(pageId, principal, cancellationToken);
        switch (result)
        {
            case CorePageAccess.NotFound:
                // No such page: no decision was made, nothing to disclose, nothing to
                // audit (ReadDenialAudit's doc).
                return null;

            case CorePageAccess.Found found:
                return new PageAccessResult(found.Page, null);

            case CorePageAccess.Denied denied:
                await ReadDenialAudit.RecordAsync(
                    auditSink, "page.view", AuditSubjectType.Page, pageId, denied.Denial.Reason, cancellationToken);
                return new PageAccessResult(null, AccessDenialView.From(denied.Denial, catalog, pageId));

            default:
                throw new InvalidOperationException($"Unexpected PageAccess case: {result.GetType().Name}.");
        }
    }
}
