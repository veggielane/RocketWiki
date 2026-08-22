using System.Text.Json;

namespace RocketWiki.Core.Events;

/// <summary>
/// Shared serialization settings for the AccessRuleChangedEvent details payload -
/// deliberately the SAME options instance used both to write DetailsJson
/// (DomainEventAuditMapper) and to read it back (AccessRuleAuditReplay), so a
/// round-trip is guaranteed by construction rather than by two call sites happening to
/// agree.
/// </summary>
internal static class AccessRuleAuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>The exact shape stored in AuditEvent.DetailsJson for a permission.change event.</summary>
internal sealed record AccessRuleChangeDetails(Guid RuleId, AccessRuleSnapshot? Before, AccessRuleSnapshot? After);
