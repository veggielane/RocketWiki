using System.Text.Json;
using System.Text.RegularExpressions;
using RocketWiki.Api.Features;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// A feature flag is read under one name and documented under others — the
/// <c>FeatureManagement:Name</c> key in docs/CONFIGURATION.md, the
/// <c>FeatureManagement__Name</c> environment variable the Helm chart renders, the
/// camel-cased values key an operator sets. Unset means ON here (RocketWikiFeatures), so
/// a name that disagrees is not "silently always-off" but "silently ignored": an operator
/// who set <c>FeatureManagement__Mcp=false</c> under a misspelt name would find the MCP
/// endpoint still mapped. Either way the failure is silent, so the spelling is pinned in
/// every place it appears, against the one list the code reads
/// (<see cref="RocketWikiFeatures.All"/>).
///
/// <para>Read as text with pinned regexes, the ApiRouteSurfaceTests method: parsing a Go
/// template or Markdown properly would mean running their toolchains, and each read has
/// a non-vacuity assertion so a reformat fails loudly instead of matching nothing.</para>
/// </summary>
public sealed class FeatureFlagNameAgreementTests
{
    private static readonly string[] ExpectedFlags = [.. RocketWikiFeatures.All];

    /// <summary>The chart's values key for a flag: lower-camel of the flag name
    /// (<c>AskWiki</c> → <c>askWiki</c>), the chart's convention for every other key.</summary>
    private static string ValuesKey(string flag) => char.ToLowerInvariant(flag[0]) + flag[1..];

    [Fact]
    public void ThereAreExactlySixFlags_AndTheyAreTheSixTheProductOwnerChose()
    {
        Assert.Equal(["AskWiki", "SemanticSearch", "GitLab", "Mcp", "CoEditing", "Sync"], ExpectedFlags);
    }

    [Fact]
    public void ConfigurationDoc_NamesEveryFlagAndNoOther()
    {
        var text = ReadRepoFile(Path.Combine("docs", "CONFIGURATION.md"));

        var documented = Regex.Matches(text, @"`FeatureManagement:(?<name>[A-Za-z]+)`")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(documented.Count > 0, "docs/CONFIGURATION.md documents no `FeatureManagement:<Name>` key; the parse is broken or the section is gone.");
        AssertSameSet(ExpectedFlags, documented, "docs/CONFIGURATION.md");
    }

    [Fact]
    public void HelmDeploymentTemplate_RendersEveryFlagAndNoOther()
    {
        var text = ReadRepoFile(Path.Combine("deploy", "helm", "rocketwiki", "templates", "api-deployment.yaml"));

        var rendered = Regex.Matches(text, @"FeatureManagement__(?<name>[A-Za-z]+)")
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(rendered.Count > 0, "api-deployment.yaml renders no FeatureManagement__<Name> variable; the parse is broken or the block is gone.");
        AssertSameSet(ExpectedFlags, rendered, "deploy/helm/rocketwiki/templates/api-deployment.yaml");

        // Each rendered variable must be fed by the matching values key, not a sibling's:
        // FeatureManagement__AskWiki reading .Values.api.features.mcp would pass the set
        // comparison above and still route the wrong switch.
        foreach (var flag in ExpectedFlags)
        {
            Assert.Matches(
                $@"FeatureManagement__{flag}\s*\n\s*value:\s*\{{\{{\s*\.Values\.api\.features\.{ValuesKey(flag)}\s*\|",
                text);
        }
    }

    [Fact]
    public void HelmValues_DeclareEveryFlagAndNoOther_AllDefaultingOn()
    {
        var text = ReadRepoFile(Path.Combine("deploy", "helm", "rocketwiki", "values.yaml"));

        // The `features:` block under `api:`: two-space-indented key, then its
        // four-space-indented children until the indentation drops back.
        var block = Regex.Match(text, @"(?m)^  features:\s*\n(?<body>(?:^    [^\n]*\n|^\s*\n|^    #[^\n]*\n)+)");
        Assert.True(block.Success, "values.yaml has no `api.features:` block; the parse is broken or the block is gone.");

        var declared = Regex.Matches(block.Groups["body"].Value, @"(?m)^    (?<key>[A-Za-z]+):\s*(?<value>true|false)\s*(#.*)?$")
            .Select(m => (Key: m.Groups["key"].Value, Value: m.Groups["value"].Value))
            .ToList();

        AssertSameSet(ExpectedFlags.Select(ValuesKey), declared.Select(d => d.Key), "deploy/helm/rocketwiki/values.yaml api.features");
        Assert.All(declared, d => Assert.True(d.Value == "true", $"api.features.{d.Key} must default to true: unset means on, and the chart's default must match the code's."));
    }

    [Fact]
    public void HelmValuesSchema_TypesEveryFlagAsBoolean_AndRefusesUnknownKeys()
    {
        using var schema = JsonDocument.Parse(ReadRepoFile(Path.Combine("deploy", "helm", "rocketwiki", "values.schema.json")));

        var features = schema.RootElement
            .GetProperty("properties").GetProperty("api")
            .GetProperty("properties").GetProperty("features");

        var declared = features.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        AssertSameSet(ExpectedFlags.Select(ValuesKey), declared, "deploy/helm/rocketwiki/values.schema.json api.features");

        Assert.All(features.GetProperty("properties").EnumerateObject(),
            p => Assert.Equal("boolean", p.Value.GetProperty("type").GetString()));

        // A misspelt flag in a values file must fail `helm lint`, not render a variable
        // the API ignores.
        Assert.False(features.GetProperty("additionalProperties").GetBoolean());
    }

    private static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual, string where)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);

        var missing = expectedSet.Except(actualSet).ToList();
        var extra = actualSet.Except(expectedSet).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"{where} disagrees with RocketWikiFeatures.All. Missing: [{string.Join(", ", missing)}]. Unknown: [{string.Join(", ", extra)}].");
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(RepoRoot.Find(), relativePath);
        Assert.True(File.Exists(path), $"Expected {relativePath} at {path}.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }
}
