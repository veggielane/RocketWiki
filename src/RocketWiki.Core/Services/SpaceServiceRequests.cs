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

/// <summary>
/// <paramref name="PageId"/> is the space's default page AFTER the call, null meaning it
/// has none. Null is "clear it", never "leave it alone": this request's whole payload is
/// the homepage, so reading null as "unspecified" would make the mutation a no-op and
/// leave no way to remove a homepage once set — the same one-way door the page icon
/// avoided by assigning unconditionally.
/// </summary>
public sealed record SetSpaceHomepageRequest(Guid SpaceId, Guid? PageId);

/// <summary>
/// Reassigns who is accountable for the space (design.md §6.5). <paramref name="OwnerUserId"/>
/// is the owner AFTER the call and is <b>not nullable</b> — the feature's premise is that
/// every space has one, so there is no "clear it" here, only "hand it to someone else".
/// </summary>
public sealed record SetSpaceOwnerRequest(Guid SpaceId, Guid OwnerUserId);

/// <summary>
/// design.md §12: flag (or unflag) this space for one-way export to a higher instance.
/// <paramref name="Exported"/> is the state AFTER the call, never a toggle — a toggle
/// makes the outcome depend on a value the caller read some time ago, which is the wrong
/// shape for a switch that decides whether content starts crossing a security boundary.
/// </summary>
public sealed record SetSpaceExportedRequest(Guid SpaceId, bool Exported);

public sealed record ArchiveSpaceRequest(Guid SpaceId);

public sealed record RestoreSpaceRequest(Guid SpaceId);
