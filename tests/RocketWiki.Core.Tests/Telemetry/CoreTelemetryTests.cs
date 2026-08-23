using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;
using Xunit;

namespace RocketWiki.Core.Tests.Telemetry;

/// <summary>
/// design.md §15/§16: instrumentation is "done" when a test proves the instrument
/// actually emits with the tags claimed, not when the call site is written. These tests
/// drive the real rule engine and read back what <see cref="CoreTelemetry"/> recorded.
///
/// They also pin the §15 boundary from the other side: the assertions below check that
/// the decision and rule kind are present AND that the matched attribute values are not.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public class CoreTelemetryTests
{
    private static Principal Principal(string[]? groups = null, Dictionary<string, string[]>? attributes = null) =>
        RocketWiki.Core.Access.Principal.Create(
            "user-1",
            groups ?? [],
            attributes?.Select(kv => new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, kv.Value)));

    private static AccessRule SpaceGrant(SpaceRole role, string expressionJson) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = Guid.NewGuid(),
        Role = role,
        ExpressionJson = expressionJson,
    };

    private static AccessRule PageRestriction(Guid pageId, PageAction action, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = action,
        ExpressionJson = expressionJson,
    };

    [Fact]
    public void MatchingSpaceGrant_RecordsAllowDecisionTaggedSpaceGrant()
    {
        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.rule_evaluations");

        EffectivePermissionCalculator.ComputeSpaceRole(
            [SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""")],
            Principal(groups: ["engineering"]));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal("space_grant", measurement.Tags[CoreTelemetry.RuleKindTag]);
        Assert.Equal(CoreTelemetry.DecisionAllow, measurement.Tags[CoreTelemetry.DecisionTag]);
    }

    [Fact]
    public void FailingPageRestriction_RecordsDenyDecisionTaggedPageRestriction()
    {
        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.rule_evaluations");

        EffectivePermissionCalculator.CheckRestrictions(
            [PageRestriction(Guid.NewGuid(), PageAction.View, """{ "attr": "nationality", "in": ["NZ"] }""")],
            PageAction.View,
            Principal(attributes: new() { ["nationality"] = ["GB"] }));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal("page_restriction", measurement.Tags[CoreTelemetry.RuleKindTag]);
        Assert.Equal(CoreTelemetry.DecisionDeny, measurement.Tags[CoreTelemetry.DecisionTag]);
    }

    [Fact]
    public void MalformedRule_RecordsMalformedDecision_DistinctFromAPlainDeny()
    {
        // design.md §6.3: a malformed rule denies like any other non-match, but is meant
        // to be noticeable. The decision tag is the only thing that separates them.
        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.rule_evaluations");

        EffectivePermissionCalculator.ComputeSpaceRole(
            [SpaceGrant(SpaceRole.Viewer, "{ this is not valid json")],
            Principal());

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(CoreTelemetry.DecisionMalformed, measurement.Tags[CoreTelemetry.DecisionTag]);
    }

    [Fact]
    public void RuleEvaluationTags_NeverCarryTheAttributeValuesBeingMatched()
    {
        // design.md §15: nationality is exactly the kind of export-controlled attribute
        // that must not reach a trace or a metric. Both the rule's allowed values and the
        // principal's own values are distinctive here, so either leaking would show up.
        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.rule_evaluations");

        EffectivePermissionCalculator.CheckRestrictions(
            [PageRestriction(Guid.NewGuid(), PageAction.View, """{ "attr": "nationality", "in": ["SENTINEL-ALLOWED"] }""")],
            PageAction.View,
            Principal(attributes: new() { ["nationality"] = ["SENTINEL-PRINCIPAL"] }));

        foreach (var measurement in collector.GetMeasurementSnapshot())
        {
            foreach (var tag in measurement.Tags)
            {
                Assert.DoesNotContain("SENTINEL", $"{tag.Key}={tag.Value}", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Compute_RecordsPermissionCheckWithCanViewCanEditAndDenialCategory()
    {
        using var checks = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.permission_checks");
        using var duration = new MetricCollector<double>(CoreTelemetry.Meter, "rocketwiki.access.permission_check.duration");

        var permission = EffectivePermissionCalculator.Compute(
            [SpaceGrant(SpaceRole.Viewer, """{ "everyone": true }""")],
            [],
            isReplicaSpace: false,
            Principal());

        Assert.True(permission.CanView);
        Assert.False(permission.CanEdit);

        var check = Assert.Single(checks.GetMeasurementSnapshot());
        Assert.Equal(true, check.Tags[CoreTelemetry.CanViewTag]);
        Assert.Equal(false, check.Tags[CoreTelemetry.CanEditTag]);
        Assert.Equal("insufficient-space-role", check.Tags[CoreTelemetry.DenialReasonTag]);

        Assert.Single(duration.GetMeasurementSnapshot());
    }

    [Fact]
    public void Compute_OnAReplica_RecordsTheReplicaReasonRatherThanARestriction()
    {
        using var checks = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.permission_checks");

        EffectivePermissionCalculator.Compute(
            [SpaceGrant(SpaceRole.SpaceAdmin, """{ "everyone": true }""")],
            [],
            isReplicaSpace: true,
            Principal());

        var check = Assert.Single(checks.GetMeasurementSnapshot());
        Assert.Equal("replica-read-only", check.Tags[CoreTelemetry.DenialReasonTag]);
    }

    [Theory]
    // The failing-restriction reason is restriction:{pageId}:{ruleId}. As a metric
    // dimension that would be one time series per rule, and it would put ids on a
    // dashboard that has no use for them - so it collapses to its shape.
    [InlineData("restriction:8a6e0804-2bd0-4672-b79d-d97027f9071a:31", "restriction")]
    [InlineData("no-space-role", "no-space-role")]
    [InlineData("replica-read-only", "replica-read-only")]
    [InlineData("insufficient-space-role", "insufficient-space-role")]
    [InlineData(null, "none")]
    [InlineData("something-new-nobody-mapped", "other")]
    public void CategorizeDenialReason_CollapsesToABoundedVocabulary(string? reason, string expected) =>
        Assert.Equal(expected, CoreTelemetry.CategorizeDenialReason(reason));

    [Fact]
    public void AuditCounter_TagsTheActionOutcomeChannelAndWriter()
    {
        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.audit.events_written");

        CoreTelemetry.RecordAuditEventWritten(
            "page.view", AuditOutcome.Denied, AuditChannel.Mcp, CoreTelemetry.AuditWriterSink);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal("page.view", measurement.Tags[CoreTelemetry.AuditActionTag]);
        Assert.Equal("Denied", measurement.Tags[CoreTelemetry.AuditOutcomeTag]);
        Assert.Equal("Mcp", measurement.Tags[CoreTelemetry.AuditChannelTag]);
        Assert.Equal(CoreTelemetry.AuditWriterSink, measurement.Tags[CoreTelemetry.AuditWriterTag]);
    }
}
