using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// Space-scoped labels. Judgement call: creating a new label definition requires at
/// least SpaceRole.Editor on the space (routine tagging, not a space-admin-only
/// taxonomy decision); attaching/detaching a label to/from a specific page requires
/// canEdit on THAT page (it's an edit of that page's metadata). Blocked on replicas,
/// same as every other mutation.
///
/// GetPagesByLabelAsync is a read path and follows the same rule as every other read
/// (design.md §6.7): a page the principal cannot view must never appear in the result,
/// not even as a count - a label listing must not reveal the existence of restricted
/// pages by any means.
/// </summary>
public interface ILabelService
{
    Task<PageMutationResult<Label>> CreateLabelAsync(
        CreateLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<PageLabel>> AttachLabelAsync(
        AttachLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Guid>> DetachLabelAsync(
        DetachLabelRequest request, Principal principal, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>Pages in the given space carrying this label that the principal can view. Never reveals a restricted page's existence, even by omission-implied count.</summary>
    Task<IReadOnlyList<Page>> GetPagesByLabelAsync(
        Guid spaceId, string labelName, Principal principal, CancellationToken cancellationToken = default);
}
