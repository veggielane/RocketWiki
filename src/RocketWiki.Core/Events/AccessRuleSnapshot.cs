using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Events;

/// <summary>
/// A full, replayable snapshot of one AccessRule's state at a point in time. Captures
/// everything design.md §6.4's evaluation actually depends on - Kind/Role/Action and
/// the conferred selectors, not just ExpressionJson - because two rules with the
/// identical expression but different kinds, roles (or a restriction's Action) mean
/// entirely different things; a replay built from ExpressionJson alone could not tell
/// an access grant from an editor grant, nor an APPLE grant from a plain one.
///
/// design.md §7 / data-model.md ("Temporal tables — considered and rejected"): SQL
/// Server temporal tables were rejected so the SQLite test tier keeps exercising the
/// real schema, which makes AuditEvent.DetailsJson the ONLY record of rule history.
/// This type is what gets embedded there on every permission.change event, and is the
/// unit AccessRuleAuditReplay reconstructs the "as of" rule set from.
///
/// <para><see cref="Selectors"/> is the LAST positional member and nullable on purpose:
/// a <c>DetailsJson</c> written before selectors existed carries no such key and
/// deserializes to null, which a replay reads as "no selectors" — the only meaning a
/// pre-selector grant could have had. A snapshot written today always carries the list
/// (empty for a role grant or a restriction), so null is unambiguously "legacy row".</para>
/// </summary>
public sealed record AccessRuleSnapshot(
    Guid AccessRuleId,
    AccessRuleKind Kind,
    Guid? SpaceId,
    Guid? PageId,
    SpaceRole? Role,
    PageAction? Action,
    string ExpressionJson,
    IReadOnlyList<SelectorValue>? Selectors = null);

public static class AccessRuleSnapshotExtensions
{
    /// <summary>The snapshot of a rule as it stands, selectors in canonical (ordinal)
    /// order so two snapshots of the same grant serialize byte-identically.</summary>
    public static AccessRuleSnapshot ToSnapshot(this AccessRule rule) =>
        new(
            rule.Id, rule.Kind, rule.SpaceId, rule.PageId, rule.Role, rule.Action, rule.ExpressionJson,
            rule.SelectorValues().OrderBy(s => s, SelectorValue.CanonicalOrder).ToArray());
}
