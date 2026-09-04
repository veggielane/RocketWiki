using System.Text.RegularExpressions;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21.2: <c>MarkingGate</c> is the one composition of the marking gates, and
/// the way a new gate gets forgotten is by one caller composing its own subset. This
/// pins that structurally, as a source sweep rather than a behaviour test: no production
/// code outside RocketWiki.Core calls <c>CaveatGate.Check</c> or
/// <c>SelectorGate.Check*</c> directly, and inside Core only the composition itself does.
/// A behaviour test could only ever prove one call site composes correctly; this proves
/// there is no other call site to get wrong. (The caveat gate was <c>ClearanceGate</c>
/// while it also compared a clearance against the level; the sweep followed the rename.)
/// </summary>
public class MarkingGateIsTheOnlyEntryPointTests
{
    private static readonly Regex PartialGateCall = new(
        @"\b(CaveatGate|SelectorGate)\.(Check|CheckGrant|CheckAll|Evaluate)\(",
        RegexOptions.Compiled);

    [Fact]
    public void NoProductionCodeOutsideCore_CallsAPartialMarkingGate()
    {
        var offenders = ProductionSources()
            .Where(path => !path.StartsWith(Path.Combine(SourceRoot(), "RocketWiki.Core") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => PartialGateCall.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(SourceRoot(), path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void InsideCore_OnlyTheCompositionAndTheGatesThemselves_CallAPartialMarkingGate()
    {
        // MarkingGate composes CaveatGate + SelectorGate; each gate may call its own
        // pieces. Anything else in Core (the calculator included) must go through
        // MarkingGate, otherwise the calculator itself could drift from the composition.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("RocketWiki.Core", "Access", "MarkingGate.cs"),
            Path.Combine("RocketWiki.Core", "Access", "CaveatGate.cs"),
            Path.Combine("RocketWiki.Core", "Access", "SelectorGate.cs"),
        };

        var offenders = ProductionSources()
            .Where(path => path.StartsWith(Path.Combine(SourceRoot(), "RocketWiki.Core") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => PartialGateCall.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(SourceRoot(), path))
            .Where(relative => !allowed.Contains(relative))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheSweep_IsNotVacuous_ItSeesTheCompositionItself()
    {
        // A sweep over an empty or misresolved directory passes every assertion above
        // while checking nothing. MarkingGate.cs must be found and must match.
        var markingGate = Path.Combine(SourceRoot(), "RocketWiki.Core", "Access", "MarkingGate.cs");

        Assert.True(File.Exists(markingGate), $"expected {markingGate} to exist");
        Assert.Matches(PartialGateCall, File.ReadAllText(markingGate));
        Assert.Contains(ProductionSources(), path => path.EndsWith(Path.Combine("Data", "Services", "PageReadService.cs"), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(SourceRoot(), path);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains("bin") && !segments.Contains("obj") && !segments.Contains("Migrations");
            });

    /// <summary>Same repo-root walk as RocketWiki.Api.Tests.RepoRoot (that helper is internal to its own assembly).</summary>
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RocketWiki.sln")))
            {
                return Path.Combine(dir.FullName, "src");
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            $"Could not locate RocketWiki.sln by walking up from '{AppContext.BaseDirectory}'.");
    }
}
