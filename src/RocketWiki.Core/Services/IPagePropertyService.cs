using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// Page properties: admin-defined keys, per-page plain-text values (design.md §20).
///
/// <para><b>Permissions.</b> Setting or removing a value on a page requires
/// <c>canEdit</c> on THAT page — it is an edit of that page's metadata, exactly like
/// attaching a label (§6.4.2). Reading needs nothing beyond <c>canView</c> on the page,
/// which the read path that resolved the page already established: properties carry no
/// restriction of their own. Both mutations sit beneath the replica invariant (§12).</para>
///
/// <para><b>Registry.</b> Creating and deleting keys is instance-admin only, mirroring
/// <see cref="ICustomEmojiService"/> — <c>isInstanceAdmin</c> is caller-resolved
/// (IInstanceRoleAccessor) for the same reason <c>actingUserId</c> is: the service
/// cannot derive it, and the gate must live here so any future caller (an MCP write
/// tool, a second GraphQL mutation) inherits it rather than reimplementing it.
/// Deleting a key that pages are still using is <b>refused</b>, naming how many pages
/// use it — silently destroying values is never the right answer for a mistyped
/// registry entry, and the admin's real recovery is to fix the pages first.</para>
///
/// <para>There is deliberately no cross-page read here (design.md §20: "not built yet").
/// When one is added it must be permission-filtered per page, exactly like
/// <c>ILabelService.GetPagesByLabelAsync</c>: a property report must not reveal a
/// restricted page's existence, not even by count (§6.7).</para>
/// </summary>
public interface IPagePropertyService
{
    Task<PageMutationResult<PagePropertyValue>> SetAsync(
        SetPagePropertyRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>Returns the removed key's id. Removing a property the page does not
    /// carry is refused (a ValidationError), not silently accepted — the same
    /// non-idempotent stance <c>ILabelService.DetachLabelAsync</c> takes.</summary>
    Task<PageMutationResult<Guid>> RemoveAsync(
        RemovePagePropertyRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<PagePropertyKey>> CreateKeyAsync(
        CreatePagePropertyKeyRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>Returns the deleted key's id. Refused with a ValidationError naming the
    /// usage count while any page still carries a value for it.</summary>
    Task<PageMutationResult<Guid>> DeleteKeyAsync(
        Guid keyId, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
