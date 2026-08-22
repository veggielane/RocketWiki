using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §8: setSpaceGrants / setPageRestrictions - space grants and page
/// restrictions are both AccessRule rows, so one CRUD-shaped service covers both kinds.
///
/// design.md §6.5.2: changing a space's own rules requires `space-admin` on that space
/// (computed from its CURRENT grants, before this change is applied) OR instance
/// `admin`. The instance-admin arm is a recovery path, not a routine one: it exists
/// for a space that reaches zero grants some other way than fresh creation - every
/// grant deleted, a half-completed import, a partial restore - which would otherwise be
/// permanently unadministrable with no fix short of hand-editing the database. (A
/// brand-new space can't reach that state at all: ISpaceService.CreateAsync commits its
/// first grant atomically - design.md §6.5.1.) This does NOT weaken design.md §6.5's
/// no-bypass principle for READS: instance admins still cannot read around page
/// restrictions, full stop. This is about who may change the rules, and every change -
/// by either arm - is audited with full before/after state, so unlike a read-around
/// (which leaves no trace), a rule change always does. <c>isInstanceAdmin</c> is a plain
/// bool the caller resolves from the token's realm/client roles, exactly as
/// ISpaceService already does.
///
/// Every method raises AccessRuleChangedEvent carrying complete before/after snapshots
/// (design.md §7) - this is the one hard audit requirement, since AuditEvent is now the
/// only record of rule history (temporal tables were rejected; data-model.md).
///
/// Deliberately not blocked on replica spaces: design.md §12 lists space grants as
/// staying local to each instance ("high decides who can view its replica"), so a
/// replica's own AccessRule rows are exactly as locally-managed as a native space's.
/// </summary>
public interface IAccessRuleService
{
    Task<PageMutationResult<AccessRule>> CreateAsync(
        CreateAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<AccessRule>> UpdateAsync(
        UpdateAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);

    Task<PageMutationResult<Guid>> DeleteAsync(
        DeleteAccessRuleRequest request, Principal principal, bool isInstanceAdmin, Guid actingUserId, AuditContext auditContext, CancellationToken cancellationToken = default);
}
