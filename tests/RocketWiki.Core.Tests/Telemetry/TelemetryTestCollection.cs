using Xunit;

namespace RocketWiki.Core.Tests.Telemetry;

/// <summary>
/// An <see cref="System.Diagnostics.Metrics.Meter"/> and an
/// <see cref="System.Diagnostics.ActivitySource"/> are process-wide: a
/// <c>MetricCollector</c> or <c>ActivityListener</c> sees every measurement and every
/// span the whole process produces, not just the ones the current test caused. xUnit
/// runs test classes in parallel by default, so without this a telemetry assertion
/// picks up whatever the rule-engine tests happen to be evaluating at the same instant
/// — which is exactly how these tests first failed: an unrelated class's malformed-rule
/// case landed in this one's snapshot.
///
/// Marking the collection non-parallelizable lets the assertions stay exact
/// ("<i>one</i> measurement, with <i>these</i> tags") instead of being weakened to
/// "contains something of roughly this shape", which would pass even if the
/// instrumentation under test emitted nothing at all.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestCollection
{
    public const string Name = "Telemetry (non-parallel)";
}
