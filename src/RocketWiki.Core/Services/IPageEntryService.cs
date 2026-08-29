using RocketWiki.Core.Access;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// docs/ENTRIES-AND-FORMS-PLAN.md: structured objects stored against a page, each with its
/// own protective marking.
///
/// <para><b>Two gates, in order, on every path.</b> First the page: `canView` to read an
/// entry, `canEdit` to write one — the page is the container, and a reader who cannot open
/// it never reaches its entries. Then the entry's own marking, per entry. The second gate
/// is the new thing: until now a page you could see was a page whose every part you could
/// see, and that is no longer true.</para>
///
/// <para><b>A pruned entry is indistinguishable from an absent one</b> (§6.7). Reads return
/// what the caller may see and say nothing about the rest — no total, no "some hidden"
/// count, no gap in numbering. That is not politeness: a count over a permission-filtered
/// set is a census of the classified estate, which is the same argument that keeps
/// classification out of RQL (§22.3), telemetry dimensions (§21.8) and the analytics
/// report (§7).</para>
/// </summary>
public interface IPageEntryService
{
    /// <summary>
    /// Every entry of one collection on one page that this caller may see, oldest first.
    ///
    /// <para>Returns <see cref="ReadResult{T}.NotFound"/> for a page that does not exist
    /// and <see cref="ReadResult{T}.Denied"/> for one the caller cannot view — collapsed
    /// by the caller into a single answer. An EMPTY list is returned both when the
    /// collection holds nothing and when it holds nothing this caller may read: those two
    /// must be indistinguishable, which is the whole of §6.7 applied here.</para>
    /// </summary>
    Task<ReadResult<IReadOnlyList<PageEntryView>>> ListAsync(
        Guid pageId, string collection, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>One entry by id. Absent, denied by page, and denied by marking are one answer.</summary>
    Task<ReadResult<PageEntryView>> GetAsync(
        Guid entryId, Principal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an entry. Needs `canEdit` on the page.
    ///
    /// <para>An explicit marking must satisfy two rules: it may not sit BELOW the page's
    /// (the page is the container, so a lower marking is a claim that will eventually be
    /// believed), and the caller may not set one they could not then read — §21.6's rule,
    /// applied per entry.</para>
    /// </summary>
    Task<PageMutationResult<PageEntryView>> CreateAsync(
        CreatePageEntryRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default);

    /// <summary>Updates an entry's data and optionally its marking. Needs `canEdit` on the
    /// page AND clearance for the entry's current marking — an entry you cannot read is an
    /// entry you cannot rewrite, including re-marking it down to make it readable.</summary>
    Task<PageMutationResult<PageEntryView>> UpdateAsync(
        UpdatePageEntryRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes an entry. Same gates as update.</summary>
    Task<PageMutationResult<PageEntryView>> DeleteAsync(
        DeletePageEntryRequest request, Principal principal, Guid actingUserId, AuditContext auditContext,
        CancellationToken cancellationToken = default);
}
