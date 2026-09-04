using System.Diagnostics;
using System.Diagnostics.Metrics;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Telemetry;

/// <summary>
/// Operational instrumentation for the domain layer (design.md §15). Plain
/// <see cref="System.Diagnostics"/> primitives, deliberately: a library declares an
/// <see cref="ActivitySource"/>/<see cref="Meter"/> and lets whichever host is running
/// decide whether to listen. Only RocketWiki.ServiceDefaults takes a dependency on the
/// OpenTelemetry packages, which is why the Importer CLI can reference this project
/// without dragging an exporter along.
///
/// **design.md §15 is a hard constraint on everything in this file.** Telemetry is not
/// audit (§7). Nothing here may carry page content, search text, or the *values* of a
/// principal's attributes — the nationality that was matched is exactly the kind of
/// export-controlled fact that must not leak into a trace. Tags below are therefore
/// enum names, decision outcomes, and bounded reason *categories* only. Identifiers
/// (page/space ids) are permitted by §15 "where needed to diagnose", and are used on
/// spans only, never as metric dimensions — a Guid tag on a counter is both a
/// cardinality bomb and a wider exposure than any dashboard needs.
/// </summary>
public static class CoreTelemetry
{
    /// <summary>
    /// Matches the assembly name, so ServiceDefaults' <c>RocketWiki.*</c> wildcard
    /// subscription picks it up without ServiceDefaults referencing this project
    /// (it can't — every project references ServiceDefaults, not the other way round).
    /// </summary>
    public const string SourceName = "RocketWiki.Core";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// One increment per <c>AccessRule</c> expression evaluated, tagged with the rule
    /// kind and what it decided. Answers "is the rule engine denying more than usual,
    /// and are malformed rules accumulating" (§6.3: a malformed rule denies and is
    /// meant to be noticed) — never which principal or which attribute value.
    /// </summary>
    public static readonly Counter<long> RuleEvaluations =
        Meter.CreateCounter<long>("rocketwiki.access.rule_evaluations", "{evaluation}",
            "AccessRule expressions evaluated, by rule kind and decision.");

    /// <summary>
    /// One increment per full canView/canEdit computation, tagged with both booleans and
    /// the coarse denial category (see <see cref="CategorizeDenialReason"/>).
    /// </summary>
    public static readonly Counter<long> PermissionChecks =
        Meter.CreateCounter<long>("rocketwiki.access.permission_checks", "{check}",
            "Effective-permission computations, by resulting canView/canEdit and denial category.");

    public static readonly Histogram<double> PermissionCheckDuration =
        Meter.CreateHistogram<double>("rocketwiki.access.permission_check.duration", "s",
            "Duration of an effective-permission computation.");

    /// <summary>
    /// Raised domain events, by CLR type name (design.md §7/§12: every mutation flows
    /// through this pipeline, so this is the closest thing to a mutation rate). Lives in
    /// Core rather than Data because the event types are Core's; RocketWikiDbContext
    /// increments it.
    /// </summary>
    public static readonly Counter<long> DomainEventsRaised =
        Meter.CreateCounter<long>("rocketwiki.domain_events.raised", "{event}",
            "Domain events raised, by event type.");

    /// <summary>
    /// Audit rows written, across both writers: the domain-event pipeline
    /// (RocketWikiDbContext, mutations) and IAuditSink (reads and denials). Tagged with
    /// the declared action name, outcome, and channel — all bounded vocabularies from
    /// data-model.md, never the subject's content. Deliberately a *count* only: the
    /// audit table itself (§7) is the record of who did what, and §15 exists to stop
    /// telemetry becoming a second, unregulated copy of it.
    /// </summary>
    public static readonly Counter<long> AuditEventsWritten =
        Meter.CreateCounter<long>("rocketwiki.audit.events_written", "{event}",
            "Audit events written, by action, outcome, channel and writer.");

    public const string RuleKindTag = "rocketwiki.access.rule_kind";
    public const string DecisionTag = "rocketwiki.access.decision";
    public const string CanViewTag = "rocketwiki.access.can_view";
    public const string CanEditTag = "rocketwiki.access.can_edit";
    public const string DenialReasonTag = "rocketwiki.access.denial_reason";
    public const string DomainEventTypeTag = "rocketwiki.domain_event.type";
    public const string AuditActionTag = "rocketwiki.audit.action";
    public const string AuditOutcomeTag = "rocketwiki.audit.outcome";
    public const string AuditChannelTag = "rocketwiki.audit.channel";
    public const string AuditWriterTag = "rocketwiki.audit.writer";

    public const string DecisionAllow = "allow";
    public const string DecisionDeny = "deny";
    public const string DecisionMalformed = "malformed";

    /// <summary>Written by RocketWikiDbContext's domain-event pipeline (mutations).</summary>
    public const string AuditWriterDomainEvent = "domain_event";

    /// <summary>Written by IAuditSink directly (reads and denials, which aren't domain events).</summary>
    public const string AuditWriterSink = "sink";

    /// <summary>Written by the SignalR hub's edit-session path (design.md §8 co-editing) —
    /// hub invocations can't use IAuditSink's HttpContext-derived context, so they write
    /// rows on AuditChannel.Realtime themselves; see EditSessionAudit in RocketWiki.Api.</summary>
    public const string AuditWriterRealtimeHub = "realtime_hub";

    public static void RecordRuleEvaluation(AccessRuleKind kind, RuleEvaluationResult result) =>
        RuleEvaluations.Add(1,
            new KeyValuePair<string, object?>(RuleKindTag, TagFor(kind)),
            new KeyValuePair<string, object?>(DecisionTag,
                result.IsMalformed ? DecisionMalformed : result.IsMatch ? DecisionAllow : DecisionDeny));

    public static void RecordPermissionCheck(EffectivePermission permission, double elapsedSeconds)
    {
        var canView = new KeyValuePair<string, object?>(CanViewTag, permission.CanView);
        var canEdit = new KeyValuePair<string, object?>(CanEditTag, permission.CanEdit);
        var reason = new KeyValuePair<string, object?>(DenialReasonTag,
            CategorizeDenialReason(permission.CanView ? permission.EditDenialReason : permission.ViewDenialReason));

        PermissionChecks.Add(1, canView, canEdit, reason);
        PermissionCheckDuration.Record(elapsedSeconds, canView, canEdit, reason);
    }

    public static void RecordAuditEventWritten(string action, AuditOutcome outcome, AuditChannel channel, string writer) =>
        AuditEventsWritten.Add(1,
            new KeyValuePair<string, object?>(AuditActionTag, action),
            new KeyValuePair<string, object?>(AuditOutcomeTag, outcome.ToString()),
            new KeyValuePair<string, object?>(AuditChannelTag, channel.ToString()),
            new KeyValuePair<string, object?>(AuditWriterTag, writer));

    /// <summary>
    /// Collapses <see cref="EffectivePermissionCalculator"/>'s denial reasons to a fixed
    /// vocabulary. The raw reason for a failed restriction is
    /// <c>restriction:{pageId}:{ruleId}</c> — fine for an audit row, wrong for a metric
    /// dimension, which would then carry one series per rule. The audit log keeps the
    /// specific reason; this keeps only the shape.
    ///
    /// <para>design.md §21's marking reasons collapse the same way: <c>caveat:eyes_only</c>
    /// becomes <c>caveat</c>, and <c>marking:unavailable</c> — a page whose marking row is
    /// missing — becomes <c>marking-unavailable</c>, which is the one marking series an
    /// operator should watch, because a non-zero count is a bug losing rows. The country
    /// set never appears in a reason string at all (see <c>CaveatGate.EyesOnlyReason</c>),
    /// and the level no longer appears in one either — it stopped gating when this
    /// deployment stopped carrying a clearance attribute — so no page id and no marking
    /// contents can reach a metric tag through this path. (While the level did gate, its
    /// token was collapsed away here for the reason the selector's still is: a
    /// "denials by level" series would have been a census of how much SECRET and TOP
    /// SECRET content exists and how hard it is being probed, published to whatever
    /// audience the dashboard has.)</para>
    /// </summary>
    public static string CategorizeDenialReason(string? denialReason) => denialReason switch
    {
        null => "none",
        "no-space-access" => "no-space-access",
        "replica-read-only" => "replica-read-only",
        "insufficient-space-role" => "insufficient-space-role",
        "marking:unavailable" => "marking-unavailable",
        _ when denialReason.StartsWith("restriction:", StringComparison.Ordinal) => "restriction",
        _ when denialReason.StartsWith("caveat:", StringComparison.Ordinal) => "caveat",
        // design.md §21.15: selector:unknown:{CATEGORY} and selector:not_granted:{CATEGORY}
        // both collapse to one word. The category name is bounded configured vocabulary,
        // but a per-category series would still be a census of which compartments exist
        // and how hard each is probed - the §21.8 argument that kept the level out,
        // applied to the selector.
        _ when denialReason.StartsWith("selector:", StringComparison.Ordinal) => "selector",
        _ => "other",
    };

    /// <summary>The rule kind as a metric dimension: the two grant kinds are distinct
    /// series because "are access grants denying more than usual" and "are role grants"
    /// are different questions (§6.4). Enum names only; never an expression.</summary>
    private static string TagFor(AccessRuleKind kind) => kind switch
    {
        AccessRuleKind.RoleGrant => "space_role_grant",
        AccessRuleKind.AccessGrant => "space_access_grant",
        AccessRuleKind.PageRestriction => "page_restriction",
        _ => "other",
    };
}
