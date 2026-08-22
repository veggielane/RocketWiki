using System.Text.Json;
using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Events;

/// <summary>
/// design.md §7: since AuditEvent is the only record of rule history (temporal tables
/// were rejected - data-model.md, "Temporal tables — considered and rejected"), the
/// rule set at any past instant must be reconstructable purely by replaying
/// `permission.change` audit rows in order. This is that replay: a reusable, pure,
/// independently-testable function - not inline test logic - since its correctness is
/// the actual compliance property design.md §7 asks for ("enforced by test").
/// </summary>
public static class AccessRuleAuditReplay
{
    /// <summary>
    /// Replays every `permission.change` event with TimestampUtc &lt;= asOfUtc, in
    /// timestamp order (ties broken by Id, matching insertion order within the same
    /// millisecond), and returns the resulting AccessRule snapshot per rule id. A rule
    /// absent from the result was either never created by this point, or had already
    /// been deleted (an event with After == null) by then. Events this instance's own
    /// audit log doesn't recognize (wrong Action, unparseable DetailsJson) are skipped
    /// rather than throwing - a corrupt or foreign row must not make the whole rule set
    /// unreconstructable.
    /// </summary>
    public static IReadOnlyDictionary<Guid, AccessRuleSnapshot> ReconstructAsOf(
        IEnumerable<AuditEvent> auditEvents, DateTime asOfUtc)
    {
        var state = new Dictionary<Guid, AccessRuleSnapshot>();

        var ordered = auditEvents
            .Where(e => e.Action == "permission.change" && e.TimestampUtc <= asOfUtc)
            .OrderBy(e => e.TimestampUtc)
            .ThenBy(e => e.Id);

        foreach (var auditEvent in ordered)
        {
            var details = TryParseDetails(auditEvent.DetailsJson);
            if (details is null)
            {
                continue;
            }

            if (details.After is null)
            {
                state.Remove(details.RuleId);
            }
            else
            {
                state[details.RuleId] = details.After;
            }
        }

        return state;
    }

    private static AccessRuleChangeDetails? TryParseDetails(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AccessRuleChangeDetails>(detailsJson, AccessRuleAuditJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
