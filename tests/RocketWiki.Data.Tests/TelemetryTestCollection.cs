using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// See the identical definition in RocketWiki.Core.Tests: an ActivitySource and a Meter
/// are process-wide, so a listener or MetricCollector in one test class also captures
/// what a class running in parallel emits. Non-parallelizable keeps these assertions
/// exact rather than forcing them down to "contains something of this shape", which
/// would still pass if the instrumentation under test emitted nothing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestCollection
{
    public const string Name = "Telemetry (non-parallel)";
}
