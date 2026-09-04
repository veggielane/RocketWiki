using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;
using RocketWiki.Core.Tests.Access;
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
        Kind = AccessRuleKind.RoleGrant,
        SpaceId = Guid.NewGuid(),
        Role = role,
        ExpressionJson = expressionJson,
    };

    private static AccessRule AccessGrant(string expressionJson) => new()
    {
        Kind = AccessRuleKind.AccessGrant,
        SpaceId = Guid.NewGuid(),
        ExpressionJson = expressionJson,
    };

    private static AccessRule PageRestriction(Guid pageId, PageAction action, string expressionJson) => new()
    {
        Kind = AccessRuleKind.PageRestriction,
        PageId = pageId,
        Action = action,
        ExpressionJson = expressionJson,
    };

    private static EffectivePermission Compute(
        IEnumerable<AccessRule> spaceGrants, IEnumerable<AccessRule> restrictions, bool isReplicaSpace, ProtectiveMarking marking, Principal principal) =>
        EffectivePermissionCalculator.Compute(
            new PermissionInputs(spaceGrants.ToList(), restrictions.ToList(), isReplicaSpace, marking, TestCatalogs.Fruit), principal);

    [Fact]
    public void MatchingSpaceGrant_RecordsAllowDecisionTaggedSpaceGrant()
    {
        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.rule_evaluations");

        EffectivePermissionCalculator.ComputeSpaceRole(
            [SpaceGrant(SpaceRole.Editor, """{ "group": "engineering" }""")],
            Principal(groups: ["engineering"]));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal("space_role_grant", measurement.Tags[CoreTelemetry.RuleKindTag]);
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
            [SpaceGrant(SpaceRole.Editor, "{ this is not valid json")],
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

        var measurements = collector.GetMeasurementSnapshot();

        // Non-vacuity first, and it matters more here than anywhere else in this file:
        // this is the guard that nationality never reaches a metric. Iterating an empty
        // snapshot passes every assertion below, so if CheckRestrictions ever stopped
        // emitting rule_evaluations, the §15 export-control guard would go green while
        // checking nothing at all. The sibling test one method up already opens with
        // Assert.Single for exactly this reason.
        Assert.NotEmpty(measurements);
        Assert.Contains(measurements, m => m.Tags.ContainsKey(CoreTelemetry.DecisionTag));

        foreach (var measurement in measurements)
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

        var permission = Compute(
            [AccessGrant("""{ "everyone": true }""")],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Baseline,
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

        Compute(
            [AccessGrant("""{ "everyone": true }"""), SpaceGrant(SpaceRole.SpaceAdmin, """{ "everyone": true }""")],
            [],
            isReplicaSpace: true,
            ProtectiveMarking.Baseline,
            Principal());

        var check = Assert.Single(checks.GetMeasurementSnapshot());
        Assert.Equal("replica-read-only", check.Tags[CoreTelemetry.DenialReasonTag]);
    }

    [Theory]
    // The failing-restriction reason is restriction:{pageId}:{ruleId}. As a metric
    // dimension that would be one time series per rule, and it would put ids on a
    // dashboard that has no use for them - so it collapses to its shape.
    [InlineData("restriction:8a6e0804-2bd0-4672-b79d-d97027f9071a:31", "restriction")]
    [InlineData("no-space-access", "no-space-access")]
    [InlineData("replica-read-only", "replica-read-only")]
    [InlineData("insufficient-space-role", "insufficient-space-role")]
    // design.md §21's marking reasons collapse the same way. The missing-row token is
    // the one marking series worth a dashboard: a non-zero count is a bug losing rows.
    [InlineData("marking:unavailable", "marking-unavailable")]
    [InlineData("caveat:eyes_only", "caveat")]
    // design.md §21.15: the two selector tokens collapse to one word, and the category
    // name is dropped: a per-category series would be a census of which compartments
    // exist and how hard each is probed, published to whatever audience the dashboard has.
    [InlineData("selector:unknown:FRUIT", "selector")]
    [InlineData("selector:not_granted:FRUIT", "selector")]
    // The level no longer gates, so its token is no longer minted; were one to arrive
    // (an old row replayed through some future path) it must not sprout a series either.
    [InlineData("classification:top_secret", "other")]
    [InlineData(null, "none")]
    [InlineData("something-new-nobody-mapped", "other")]
    public void CategorizeDenialReason_CollapsesToABoundedVocabulary(string? reason, string expected) =>
        Assert.Equal(expected, CoreTelemetry.CategorizeDenialReason(reason));

    [Fact]
    public void CategorizeDenialReason_NeverLetsAMarkingLevelOrCountryReachAMetricTag()
    {
        // The countries never appear in a reason string at all (CaveatGate.EyesOnlyReason
        // is a constant), the level appears in none since it stopped gating, and a stray
        // level token is collapsed to "other" rather than given a series. Together that is
        // what keeps the marking's contents out of every metric dimension (design.md §15).
        Assert.DoesNotContain("secret", CoreTelemetry.CategorizeDenialReason("classification:top_secret"));
        Assert.DoesNotContain("GB", CoreTelemetry.CategorizeDenialReason("caveat:eyes_only"));
        Assert.DoesNotContain("FRUIT", CoreTelemetry.CategorizeDenialReason("selector:not_granted:FRUIT"));
    }

    [Fact]
    public void PermissionCheck_OnACaveatDenial_TagsOnlyTheCollapsedCategory()
    {
        using var checks = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.permission_checks");

        Compute(
            [AccessGrant("""{ "everyone": true }"""), SpaceGrant(SpaceRole.SpaceAdmin, """{ "everyone": true }""")],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.TopSecret, ["SENTINELCOUNTRY"]),
            Principal());

        var check = Assert.Single(checks.GetMeasurementSnapshot());
        Assert.Equal("caveat", check.Tags[CoreTelemetry.DenialReasonTag]);
        foreach (var tag in check.Tags)
        {
            Assert.DoesNotContain("SENTINELCOUNTRY", $"{tag.Key}={tag.Value}", StringComparison.OrdinalIgnoreCase);
            // The level rides on the marking and never reaches a tag - it is not even
            // a reason any more.
            Assert.DoesNotContain("top_secret", $"{tag.Key}={tag.Value}", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PermissionCheck_OnAnUnavailableMarking_TagsTheMissingRowSeries()
    {
        // The one marking series an operator should alert on: a page with no marking row
        // is a bug losing rows, and the tag says so without naming the page.
        using var checks = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.permission_checks");

        Compute(
            [AccessGrant("""{ "everyone": true }""")],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.FailClosed,
            Principal());

        var check = Assert.Single(checks.GetMeasurementSnapshot());
        Assert.Equal("marking-unavailable", check.Tags[CoreTelemetry.DenialReasonTag]);
        Assert.Equal(false, check.Tags[CoreTelemetry.CanViewTag]);
    }

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

    [Fact]
    public void PermissionCheck_OnASelectorDenial_TagsOnlyTheCollapsedCategory_NeverTheCategoryNameOrValue()
    {
        // design.md §15/§21.15: a selector category is bounded configured vocabulary, but a
        // per-category series would be a census of which compartments exist and how hard
        // each is probed. Neither the category nor the value may reach a metric tag.
        using var checks = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.permission_checks");
        using var evaluations = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.rule_evaluations");

        var permission = Compute(
            [AccessGrant("""{ "everyone": true }""")],
            [],
            isReplicaSpace: false,
            ProtectiveMarking.Create(ClassificationLevel.Official, null, [new SelectorValue("FRUIT", "SENTINEL-VALUE")]),
            Principal(attributes: new() { ["nationality"] = ["SENTINEL-CLAIM"] }));

        Assert.False(permission.CanView);
        Assert.Equal("selector:not_granted:FRUIT", permission.ViewDenialReason);

        var check = Assert.Single(checks.GetMeasurementSnapshot());
        Assert.Equal("selector", check.Tags[CoreTelemetry.DenialReasonTag]);
        foreach (var measurement in checks.GetMeasurementSnapshot().Concat(evaluations.GetMeasurementSnapshot()))
        {
            foreach (var tag in measurement.Tags)
            {
                Assert.DoesNotContain("SENTINEL", $"{tag.Key}={tag.Value}", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("FRUIT", $"{tag.Key}={tag.Value}", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("APPLE", $"{tag.Key}={tag.Value}", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
