using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.5.1: Create requires instance `admin` - nothing else can authorize it,
/// since a space that doesn't exist yet has no grants to evaluate a role against. Create
/// also takes the space's initial grants (<see cref="InitialGrant"/>) and commits every
/// one of them in the SAME transaction as the Space row, each with its own audited
/// <c>permission.change</c> - see that type's doc comment for why: without this, a new
/// space could exist with zero grants and no way for anyone to ever create the first
/// one, since AccessRuleService.CreateAsync's own space-admin check has nothing to
/// evaluate against on a space that has never had a grant. The list must contain at
/// least one <c>SpaceAdmin</c> role grant (a <c>ValidationError</c> otherwise): a space
/// is born administrable. Access grants are optional — a space nobody can see yet is the
/// correct starting state, since roles confer no visibility (§6.4).
///
/// Rename, Archive, and Restore all accept EITHER instance `admin` OR that space's own
/// `space-admin` (evaluated the normal way via EffectivePermissionCalculator), matching
/// `space-admin` meaning "manage this space". <c>isInstanceAdmin</c> is a plain bool the
/// caller resolves from the token's realm/client roles, exactly like
/// <c>actingUserId</c> is already caller-resolved from the `sub` claim (design.md §11) -
/// this service has no way to derive it itself.
///
/// Archive does not cascade to pages (design.md §6.5.1): it hides the space from browse
/// and makes it read-only, content untouched, and Restore reverses it - unlike page
/// delete, which removes content and therefore needs canEdit per page.
/// </summary>
public interface ISpaceService
{
    Task<PageMutationResult<Space>> CreateAsync(
        CreateSpaceRequest request, IReadOnlyList<InitialGrant> initialGrants, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Space>> RenameAsync(
        RenameSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears the space's default page (design.md §6.5.1's same gate as Rename).
    /// The target must be a LIVE page in THIS space: a homepage pointing at a trashed
    /// page is a link that resolves to nothing, and one pointing into another space is a
    /// cross-space reference the space's own admin was never authorized to make. Both are
    /// refused with a <c>ValidationError</c> rather than silently ignored.
    /// </summary>
    Task<PageMutationResult<Space>> SetHomepageAsync(
        SetSpaceHomepageRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reassigns the space's designated owner — <b>accountability metadata, never access</b>
    /// (design.md §6.5). Nothing in the rule engine reads <c>OwnerUserId</c>; an owner who
    /// should also administer the space gets a space-admin grant, separately and explicitly.
    ///
    /// <para><b>Gated on canManageAccess</b> (instance admin OR this space's space admin,
    /// §6.5.2) rather than on being the current owner. Ownership is not a right its holder
    /// controls — that would make it self-perpetuating and let a departing owner lock the
    /// space's accountability to themselves. It is an administrative designation, so the
    /// people who administer the space assign it.</para>
    ///
    /// <para><b>No <c>ReadOnlyReplicaError</c> arm</b>, matching Rename/Archive/Restore and
    /// SetHomepage rather than SetExported. §12's table puts space lifecycle and identity in
    /// the "stays local" column, and ownership is squarely that. It also has to be settable
    /// on a replica to be settable at all: the importer materialises replica spaces with
    /// <c>CreatedByUserId = Guid.Empty</c>, users never cross the boundary (each side runs
    /// its own Keycloak), so a replica that refused this would be permanently ownerless —
    /// on the side where accountability for imported content matters most.</para>
    ///
    /// <para>The new owner must be a real, non-deleted local user; anything else is a
    /// <c>ValidationError</c>. Assigning ownership to nobody is precisely the state this
    /// feature exists to eliminate.</para>
    /// </summary>
    Task<PageMutationResult<Space>> SetOwnerAsync(
        SetSpaceOwnerRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// design.md §12's low-side switch: flags this space for one-way export, which is
    /// what makes the outbox writer start journaling its mutations and what
    /// <c>RocketWiki.Sync export --baseline</c> requires before it will produce a
    /// baseline bundle. Until this existed, nothing in the product could set
    /// <c>Space.IsExported</c> at all — both writers set it to <c>false</c> — so the
    /// whole §12 pipeline was reachable only by hand-editing the database, and the CLI's
    /// own refusal message told operators to "flag it exported first" with no way to.
    ///
    /// <para><b>Instance admin only</b>, and deliberately NOT the "instance admin or
    /// space admin" gate Rename/Archive/Restore use. Those curate a space inside this
    /// instance; this one decides that a space's content starts crossing into another
    /// security domain, which is an instance-level judgement about the boundary rather
    /// than about the space. §6.5 gives instance admins "manage spaces" and this is the
    /// sharpest thing in that category.</para>
    ///
    /// <para><b>A replica can never be flagged exported.</b> §12: "Only a native space
    /// can be exported; a replica must never emit sync events for content it doesn't
    /// own." The outbox writer already treats replica-flagged-exported as corrupt state
    /// it refuses to journal; this refuses to create that state in the first place, with
    /// a <c>ReadOnlyReplicaError</c> — the same answer every other write to a replica
    /// gets.</para>
    /// </summary>
    Task<PageMutationResult<Space>> SetExportedAsync(
        SetSpaceExportedRequest request, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Space>> ArchiveAsync(
        ArchiveSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Space>> RestoreAsync(
        RestoreSpaceRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
