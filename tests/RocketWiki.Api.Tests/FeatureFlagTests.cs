using Microsoft.Extensions.Configuration;
using Microsoft.FeatureManagement;
using RocketWiki.Api.Features;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The evaluation rules behind <see cref="FeatureFlagSnapshot"/> (docs/CONFIGURATION.md
/// "Feature flags"), on configuration alone — no host. The one that matters most is the
/// first: with no <c>FeatureManagement</c> section at all, every flag is ON, so this
/// change is a no-op on every configuration that existed before it. The library's own
/// default for an unmentioned flag is off; the rest of this class pins the provider that
/// inverts it for exactly the six known names and nothing else.
/// </summary>
public sealed class FeatureFlagTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    public static TheoryData<string> EveryFlag() => new(RocketWikiFeatures.All);

    [Fact]
    public void WithNoFeatureManagementSection_EveryFlagIsOn()
    {
        var snapshot = FeatureFlagSnapshot.Evaluate(Config());

        Assert.Equal(FeatureFlagSnapshot.AllEnabled, snapshot);
        // Non-vacuous: six flags, all named, all on.
        Assert.Equal(6, RocketWikiFeatures.All.Count);
        Assert.All(RocketWikiFeatures.All, flag => Assert.True(snapshot.IsEnabled(flag), flag));
    }

    /// <summary>Each flag switches only itself — a wrong mapping in the snapshot (two
    /// properties reading one name) would show up here as a second false.</summary>
    [Theory]
    [MemberData(nameof(EveryFlag))]
    public void AFlagSetFalse_TurnsOffThatFlagAndNoOther(string flag)
    {
        var snapshot = FeatureFlagSnapshot.Evaluate(Config(($"FeatureManagement:{flag}", "false")));

        Assert.False(snapshot.IsEnabled(flag), flag);
        foreach (var other in RocketWikiFeatures.All.Where(f => f != flag))
        {
            Assert.True(snapshot.IsEnabled(other), $"{other} must stay on when only {flag} is off");
        }
    }

    [Theory]
    [MemberData(nameof(EveryFlag))]
    public void AFlagSetTrue_IsOn(string flag)
    {
        var snapshot = FeatureFlagSnapshot.Evaluate(Config(($"FeatureManagement:{flag}", "true")));

        Assert.True(snapshot.IsEnabled(flag), flag);
    }

    /// <summary>
    /// The trap the provider exists to avoid. The stock provider, as
    /// <c>AddFeatureManagement()</c> registers it, falls back to reading the ROOT of
    /// configuration when no <c>FeatureManagement</c> section exists — and this instance
    /// has a root section called <c>GitLab</c>. Configuring GitLab would then have
    /// defined the GitLab FLAG as a section with no <c>EnabledFor</c>, i.e. off. Both
    /// halves are asserted: the stock fallback really does say off (so this test is about
    /// a real behaviour, not a hypothetical), and ours says on.
    /// <para>Mutation-tested: set RootConfigurationFallbackEnabled = true in the provider
    /// and the second assertion fails.</para>
    /// </summary>
    [Fact]
    public void ARootSectionNamedLikeAFlag_DoesNotDefineTheFlag()
    {
        var configuration = Config(("GitLab:BaseUrl", "https://gitlab.test"), ("GitLab:TimeoutSeconds", "5"));

        var stockWithFallback = new FeatureManager(
            new ConfigurationFeatureDefinitionProvider(configuration) { RootConfigurationFallbackEnabled = true });
        Assert.False(stockWithFallback.IsEnabledAsync(RocketWikiFeatures.GitLab).GetAwaiter().GetResult(),
            "The stock provider's root fallback no longer reads the GitLab section as a flag; " +
            "this test's premise has changed — re-read RocketWikiFeatureDefinitionProvider's doc.");

        Assert.True(FeatureFlagSnapshot.Evaluate(configuration).GitLab);
    }

    /// <summary>Unset-means-on is for the six known names only; the library's answer for
    /// anything else (off) is untouched, so nothing can be switched on by being unnamed.</summary>
    [Fact]
    public void AnUnknownFeatureName_IsNotDefaultedOn()
    {
        var manager = new FeatureManager(new RocketWikiFeatureDefinitionProvider(Config()));

        Assert.False(manager.IsEnabledAsync("NotARocketWikiFeature").GetAwaiter().GetResult());
        Assert.Throws<ArgumentOutOfRangeException>(() => FeatureFlagSnapshot.AllEnabled.IsEnabled("NotARocketWikiFeature"));
    }

    /// <summary>The library's own schema keeps working through the provider: the explicit
    /// <c>EnabledFor: [AlwaysOn]</c> form is what a plain <c>true</c> means.</summary>
    [Fact]
    public void TheLibrarysEnabledForSchema_IsHonoured()
    {
        var on = FeatureFlagSnapshot.Evaluate(Config(("FeatureManagement:Mcp:EnabledFor:0:Name", "AlwaysOn")));
        Assert.True(on.Mcp);

        // A section with no filters is the library's "disabled" — the shape the stock
        // root fallback would have produced from GitLab:BaseUrl.
        var off = FeatureFlagSnapshot.Evaluate(Config(("FeatureManagement:Mcp:RequirementType", "Any")));
        Assert.False(off.Mcp);
    }

    /// <summary>
    /// A flag written with a contextual or time-based filter has no meaning for a switch
    /// evaluated once at startup — a TimeWindow that "ends at 17:00" would end at the next
    /// restart — so the evaluation refuses it loudly (the library's missing-filter
    /// exception, naming the filter) rather than guessing. Fail closed: the host does not
    /// boot on a flag it cannot honour.
    /// </summary>
    [Fact]
    public void AFilterBasedDefinition_FailsEvaluationRatherThanBeingGuessedAt()
    {
        var configuration = Config(
            ("FeatureManagement:CoEditing:EnabledFor:0:Name", "TimeWindow"),
            ("FeatureManagement:CoEditing:EnabledFor:0:Parameters:End", "Mon, 01 Jan 2035 00:00:00 GMT"));

        var thrown = Assert.ThrowsAny<Exception>(() => FeatureFlagSnapshot.Evaluate(configuration));
        Assert.Contains("TimeWindow", thrown.ToString(), StringComparison.Ordinal);
    }

    /// <summary>GetAllFeatureDefinitionsAsync — what <c>IFeatureManager.GetFeatureNamesAsync</c>
    /// enumerates — lists every known flag once, configured or not.</summary>
    [Fact]
    public async Task EveryKnownFlag_IsEnumeratedExactlyOnce()
    {
        var provider = new RocketWikiFeatureDefinitionProvider(Config(("FeatureManagement:Sync", "false")));

        var names = new List<string>();
        await foreach (var definition in provider.GetAllFeatureDefinitionsAsync())
        {
            names.Add(definition.Name);
        }

        Assert.Equal(RocketWikiFeatures.All.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
    }
}
