using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// Registry mutations for custom emojis (design.md §6.5: the registry is instance-admin
/// vocabulary, like the attribute registry — <i>using</i> an emoji needs no permission
/// beyond canEdit on the page it's typed into, which the page save already enforces).
///
/// <c>isInstanceAdmin</c> is caller-resolved (IInstanceRoleAccessor), the same pattern
/// as ISpaceService/IAccessRuleService — the service cannot derive it itself, and the
/// gate must live here so any future caller (an MCP write tool, a GraphQL mutation)
/// inherits it rather than reimplementing it. Both mutations flow through the
/// domain-event pipeline (emoji.created / emoji.deleted, same-transaction audit).
///
/// Reads have no service: the serve route and the customEmojis list query are plain
/// lookups over a table any authenticated user may see in full — there is no
/// per-subject access decision to encapsulate (contrast IPageReadService).
/// </summary>
public interface ICustomEmojiService
{
    Task<PageMutationResult<CustomEmoji>> CreateAsync(
        CreateCustomEmojiRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<CustomEmoji>> DeleteAsync(
        string name, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="ImageBytes"/>/<paramref name="ContentType"/>/<paramref name="PixelSize"/>
/// are the <b>already-normalized</b> output of the API layer's image processing
/// (EmojiImageProcessor: decoded under limits, squared, capped, re-encoded, metadata
/// stripped) — this service stores what it is given and never sees the upload's raw
/// bytes. Name grammar is still (re-)checked here: the service is the enforcement
/// point, the route merely fails fast.
/// </summary>
public sealed record CreateCustomEmojiRequest(string Name, byte[] ImageBytes, string ContentType, int PixelSize);
