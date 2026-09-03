using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Services;

public sealed record CreateSpaceRequest(string Key, string Name, string? Description);

/// <summary>
/// design.md §6.5.1: one of the grants a brand-new space is born with - required, never
/// defaulted. Before this existed, a freshly created Space had zero AccessRule rows and
/// no way to create the first one through the public service surface, since
/// AccessRuleService.CreateAsync requires the caller to already be that space's
/// space-admin (computed from the space's CURRENT grants) - unsatisfiable when there are
/// none. ISpaceService.CreateAsync commits the whole list in the SAME transaction as the
/// Space row, and refuses a list without a <see cref="SpaceRole.SpaceAdmin"/> role grant,
/// so a space never exists in a state nobody can administer.
///
/// <para>Access grants (<see cref="AccessRuleKind.AccessGrant"/>) are optional here, and
/// there is deliberately no default for them either: a new space nobody can see is the
/// correct starting state (§6.4 — roles confer no visibility), and silently opening one to
/// "everyone" is exactly the footgun this access model exists to prevent. The caller
/// states each grant.</para>
/// </summary>
/// <param name="Kind">A role grant (<see cref="AccessRuleKind.RoleGrant"/>) or an access
/// grant; a page restriction is meaningless at space creation and is refused.</param>
/// <param name="Role">Required on a role grant, must be null on an access grant.</param>
/// <param name="ExpressionJson">The subject rule, validated like any other rule's.</param>
/// <param name="SelectorValues">Access grants only: the selector values conferred (§21.15),
/// each a configured category/value pair.</param>
public sealed record InitialGrant(
    AccessRuleKind Kind,
    SpaceRole? Role,
    string ExpressionJson,
    IReadOnlyList<SelectorValue>? SelectorValues = null);

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
