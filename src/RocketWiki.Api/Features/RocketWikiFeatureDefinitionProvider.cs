using Microsoft.FeatureManagement;

namespace RocketWiki.Api.Features;

/// <summary>
/// Microsoft.FeatureManagement's configuration-backed definition provider, with one rule
/// added: a flag in <see cref="RocketWikiFeatures.All"/> that the <c>FeatureManagement</c>
/// section does not mention is <b>on</b>. The library's own answer for a missing feature
/// is off (<c>IgnoreMissingFeatures</c> returns false rather than throwing), which would
/// have turned every optional feature off for every existing deployment the moment this
/// version shipped. Configuration always wins when it says anything at all — a plain
/// <c>true</c>/<c>false</c>, or the library's full <c>EnabledFor</c> filter schema — and
/// unknown names keep the library's behaviour, so nothing outside the six is defaulted on.
///
/// <para><b>Root-configuration fallback is deliberately off.</b> The stock provider, as
/// <c>AddFeatureManagement()</c> registers it, falls back to reading feature definitions
/// from the ROOT of configuration when no <c>FeatureManagement</c> section exists. This
/// instance has a root section named <c>GitLab</c>; with fallback on, <c>GitLab:BaseUrl</c>
/// would have been parsed as the <c>GitLab</c> flag's definition — a section with no
/// <c>EnabledFor</c>, i.e. disabled — and configuring GitLab would have switched GitLab
/// off. FeatureFlagTests pins that a root <c>GitLab</c> section leaves the flag on.</para>
/// </summary>
public sealed class RocketWikiFeatureDefinitionProvider : IFeatureDefinitionProvider
{
    private readonly ConfigurationFeatureDefinitionProvider _inner;

    public RocketWikiFeatureDefinitionProvider(IConfiguration configuration)
    {
        _inner = new ConfigurationFeatureDefinitionProvider(configuration)
        {
            RootConfigurationFallbackEnabled = false,
        };
    }

    public async Task<FeatureDefinition> GetFeatureDefinitionAsync(string featureName)
    {
        var configured = await _inner.GetFeatureDefinitionAsync(featureName);
        if (configured is not null)
        {
            return configured;
        }

        // null is the library's own "no such feature" answer, which FeatureManager turns
        // into false (IgnoreMissingFeatures); the interface predates nullable annotations.
        return IsKnown(featureName) ? AlwaysOn(featureName) : null!;
    }

    public async IAsyncEnumerable<FeatureDefinition> GetAllFeatureDefinitionsAsync()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var definition in _inner.GetAllFeatureDefinitionsAsync())
        {
            seen.Add(definition.Name);
            yield return definition;
        }

        foreach (var name in RocketWikiFeatures.All.Where(name => !seen.Contains(name)))
        {
            yield return AlwaysOn(name);
        }
    }

    private static bool IsKnown(string featureName) =>
        RocketWikiFeatures.All.Contains(featureName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The definition the library itself produces for a plain <c>true</c>: one
    /// <c>AlwaysOn</c> filter, which FeatureManager special-cases without a filter type.</summary>
    private static FeatureDefinition AlwaysOn(string name) => new()
    {
        Name = name,
        EnabledFor = [new FeatureFilterConfiguration { Name = "AlwaysOn" }],
    };
}
