using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

public sealed record CreateSpaceRequest(string Key, string Name, string? Description);

/// <summary>
/// design.md §6.5.1: the grant a brand-new space is born with - required, never
/// defaulted. Before this existed, a freshly created Space had zero AccessRule rows and
/// no way to create the first one through the public service surface, since
/// AccessRuleService.CreateAsync requires the caller to already be that space's
/// space-admin (computed from the space's CURRENT grants) - unsatisfiable when there are
/// none. ISpaceService.CreateAsync now commits this grant in the SAME transaction as the
/// Space row, so a space never exists in a state nobody can administer. There is
/// deliberately no default: silently opening a new space to "everyone" is exactly the
/// footgun this access model exists to prevent, so the caller must decide.
/// </summary>
public sealed record InitialSpaceGrant(SpaceRole Role, string ExpressionJson);

public sealed record RenameSpaceRequest(Guid SpaceId, string Name, string? Description);

public sealed record ArchiveSpaceRequest(Guid SpaceId);

public sealed record RestoreSpaceRequest(Guid SpaceId);
