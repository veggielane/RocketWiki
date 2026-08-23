using Xunit;

namespace RocketWiki.SqlServer.Tests;

/// <summary>
/// [Fact] for tests that need the shared SQL Server container: skips - visibly, with
/// the probe's reason - when no working Docker daemon exists, so `dotnet test
/// RocketWiki.sln` stays green on machines without a container runtime (design.md
/// §16's standing caveat) while CI, which has Docker, runs everything and separately
/// fails if anything here skipped. Every test in this project must use this attribute
/// (or extend it), never plain [Fact]: a plain [Fact] would turn "no Docker" from a
/// skip into a failure and break the solution-wide run this repo promises stays green.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = $"{DockerAvailability.SkipReason} This SQL Server tier (design.md §14 tier 3) " +
                   "only runs where Docker works - first-class execution is CI's sqlserver job.";
        }
    }
}
