using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Events;

/// <summary>
/// A full, replayable snapshot of one AccessRule's state at a point in time. Captures
/// everything design.md §6.4's evaluation actually depends on - Kind/Role/Action, not
/// just ExpressionJson - because two rules with the identical expression but different
/// roles (or a restriction's Action) mean entirely different things; a replay built from
/// ExpressionJson alone could not tell a viewer grant from an editor grant.
///
/// design.md §7 / data-model.md ("Temporal tables — considered and rejected"): SQL
/// Server temporal tables were rejected so the SQLite test tier keeps exercising the
/// real schema, which makes AuditEvent.DetailsJson the ONLY record of rule history.
/// This type is what gets embedded there on every permission.change event, and is the
/// unit AccessRuleAuditReplay reconstructs the "as of" rule set from.
/// </summary>
public sealed record AccessRuleSnapshot(
    Guid AccessRuleId,
    AccessRuleKind Kind,
    Guid? SpaceId,
    Guid? PageId,
    SpaceRole? Role,
    PageAction? Action,
    string ExpressionJson);

public static class AccessRuleSnapshotExtensions
{
    public static AccessRuleSnapshot ToSnapshot(this AccessRule rule) =>
        new(rule.Id, rule.Kind, rule.SpaceId, rule.PageId, rule.Role, rule.Action, rule.ExpressionJson);
}
