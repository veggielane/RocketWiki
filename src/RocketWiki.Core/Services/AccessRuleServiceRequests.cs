using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

/// <summary>
/// The columns must pair with the kind exactly as <c>CK_AccessRules_KindColumnPairing</c>
/// requires (data-model.md): a role grant sets SpaceId + Role (Editor or SpaceAdmin); an
/// access grant sets SpaceId alone and may carry <paramref name="SelectorValues"/>
/// (design.md §21.15 — each must be a configured category/value pair); a page
/// restriction sets PageId + Action. Selectors on anything but an access grant are a
/// <c>ValidationError</c>.
/// </summary>
public sealed record CreateAccessRuleRequest(
    AccessRuleKind Kind,
    Guid? SpaceId,
    Guid? PageId,
    SpaceRole? Role,
    PageAction? Action,
    string ExpressionJson,
    IReadOnlyList<SelectorValue>? SelectorValues = null);

/// <summary>
/// Kind/SpaceId/PageId are a rule's fixed identity and cannot be changed by an update -
/// only its expression and (for the matching kind) Role or Action. For an access grant,
/// <paramref name="SelectorValues"/> is the FULL replacement set: null leaves the
/// conferred selectors as they are, an empty list clears them.
/// </summary>
public sealed record UpdateAccessRuleRequest(
    Guid AccessRuleId,
    string ExpressionJson,
    SpaceRole? Role,
    PageAction? Action,
    IReadOnlyList<SelectorValue>? SelectorValues = null);

public sealed record DeleteAccessRuleRequest(Guid AccessRuleId);
