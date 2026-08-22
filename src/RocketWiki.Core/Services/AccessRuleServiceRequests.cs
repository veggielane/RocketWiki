using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

/// <summary>Exactly one of (SpaceId, Role) / (PageId, Action) must be set, matching AccessRule's own kind↔column pairing (data-model.md, CK_AccessRules_KindColumnPairing).</summary>
public sealed record CreateAccessRuleRequest(AccessRuleKind Kind, Guid? SpaceId, Guid? PageId, SpaceRole? Role, PageAction? Action, string ExpressionJson);

/// <summary>Kind/SpaceId/PageId are a rule's fixed identity and cannot be changed by an update - only its expression and (for the matching kind) Role or Action.</summary>
public sealed record UpdateAccessRuleRequest(Guid AccessRuleId, string ExpressionJson, SpaceRole? Role, PageAction? Action);

public sealed record DeleteAccessRuleRequest(Guid AccessRuleId);
