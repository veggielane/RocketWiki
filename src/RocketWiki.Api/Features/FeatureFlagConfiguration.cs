using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FeatureManagement;

namespace RocketWiki.Api.Features;

/// <summary>
/// Wires feature flags (docs/CONFIGURATION.md "Feature flags"): evaluates the six
/// <see cref="RocketWikiFeatures"/> once from the builder's configuration, registers the
/// result as the process-wide <see cref="FeatureFlagSnapshot"/>, and registers
/// Microsoft.FeatureManagement's <see cref="IFeatureManager"/> over the same
/// <see cref="RocketWikiFeatureDefinitionProvider"/>.
///
/// <para>Read EAGERLY off the builder, deliberately and knowingly. The snapshot has to
/// exist before <c>Build()</c> because three registrations depend on it (see
/// FeatureFlagSnapshot's doc), and an eager read is the ordering trap this codebase has
/// been bitten by: a <c>WebApplicationFactory</c>'s <c>ConfigureAppConfiguration</c>
/// lands after top-level statements have run. The test tier therefore sets flags with
/// <c>UseSetting</c>, which travels as a command-line argument and IS visible here —
/// FeatureFlagOffStateTests documents the mechanism. Production reads the same values
/// either way.</para>
///
/// <para><see cref="IFeatureManager"/> is registered for two reasons: it is the library's
/// evaluation service and the snapshot is defined as "what it answers at boot"
/// (FeatureFlagTests pins the agreement, so a provider-registration mistake cannot make
/// the two disagree silently); and it is the seam a genuinely per-request flag would use
/// if one ever appears. Nothing evaluates a flag per request today.</para>
/// </summary>
public static class FeatureFlagConfiguration
{
    public static FeatureFlagSnapshot AddRocketWikiFeatureFlags(this WebApplicationBuilder builder)
    {
        var snapshot = FeatureFlagSnapshot.Evaluate(builder.Configuration);
        builder.Services.AddSingleton(snapshot);

        builder.Services.AddFeatureManagement();
        // Replace, not TryAdd: whichever way the library registers its own provider, the
        // one the container resolves must be ours — root-configuration fallback off and
        // unset-means-on for the six known flags (see the provider's doc for why the
        // stock fallback is unsafe on this configuration).
        builder.Services.Replace(ServiceDescriptor.Singleton<IFeatureDefinitionProvider>(
            sp => new RocketWikiFeatureDefinitionProvider(sp.GetRequiredService<IConfiguration>())));

        return snapshot;
    }
}
