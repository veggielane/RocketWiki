using Microsoft.FeatureManagement;

namespace RocketWiki.Api.Features;

/// <summary>
/// The six flags of <see cref="RocketWikiFeatures"/>, evaluated <b>once, at startup</b>,
/// and handed to every surface that needs an answer — the registration decisions in
/// Program.cs (chat client, embedding pipeline, MCP endpoint) and the per-request
/// surfaces alike (GitLab options, the co-edit join, the sync fields).
///
/// <para>Why one startup evaluation rather than <c>IFeatureManager</c> per request: three
/// of these features are decided at registration time by necessity (a client that does
/// not exist cannot send content — the structural posture every fail-closed feature here
/// already takes), so those three can only ever change with a restart. Evaluating the
/// other three live would let an <c>appsettings.json</c> edit flip <c>gitlabStatus</c>
/// while the chat client decided at boot stayed put — two answers to "what is on here"
/// from one process. Instead every flag behaves like every other configuration key in
/// docs/CONFIGURATION.md: read at startup, restart to apply. <c>IFeatureManager</c> stays
/// registered (FeatureFlagConfiguration) and FeatureFlagTests pins that it agrees with
/// this snapshot for every flag, so the two evaluation paths cannot quietly diverge.</para>
///
/// <para>The evaluation goes through the library's own <see cref="FeatureManager"/> over
/// <see cref="RocketWikiFeatureDefinitionProvider"/>, so a flag written in the full
/// <c>EnabledFor</c> filter schema is honoured exactly as the library would honour it.
/// The blocking wait inside <see cref="Evaluate(IFeatureManager)"/> never waits on I/O:
/// the configuration-backed provider and the <c>AlwaysOn</c> filter complete
/// synchronously, and this is the one place in the process that calls it.</para>
/// </summary>
public sealed record FeatureFlagSnapshot(
    bool AskWiki,
    bool SemanticSearch,
    bool GitLab,
    bool Mcp,
    bool CoEditing,
    bool Sync)
{
    /// <summary>What an unset section evaluates to — and what wiring tests pass when
    /// flags are not what they are about.</summary>
    public static readonly FeatureFlagSnapshot AllEnabled = new(true, true, true, true, true, true);

    public bool IsEnabled(string flagName) => flagName switch
    {
        RocketWikiFeatures.AskWiki => AskWiki,
        RocketWikiFeatures.SemanticSearch => SemanticSearch,
        RocketWikiFeatures.GitLab => GitLab,
        RocketWikiFeatures.Mcp => Mcp,
        RocketWikiFeatures.CoEditing => CoEditing,
        RocketWikiFeatures.Sync => Sync,
        _ => throw new ArgumentOutOfRangeException(nameof(flagName), flagName, "Not a RocketWiki feature flag."),
    };

    /// <summary>Evaluates the flags from a configuration root — the pre-<c>Build()</c> path
    /// Program.cs takes, because three registration decisions need the answer before a
    /// container exists.</summary>
    public static FeatureFlagSnapshot Evaluate(IConfiguration configuration) =>
        Evaluate(new FeatureManager(new RocketWikiFeatureDefinitionProvider(configuration)));

    public static FeatureFlagSnapshot Evaluate(IFeatureManager featureManager)
    {
        bool Flag(string name) => featureManager.IsEnabledAsync(name).GetAwaiter().GetResult();

        return new FeatureFlagSnapshot(
            AskWiki: Flag(RocketWikiFeatures.AskWiki),
            SemanticSearch: Flag(RocketWikiFeatures.SemanticSearch),
            GitLab: Flag(RocketWikiFeatures.GitLab),
            Mcp: Flag(RocketWikiFeatures.Mcp),
            CoEditing: Flag(RocketWikiFeatures.CoEditing),
            Sync: Flag(RocketWikiFeatures.Sync));
    }
}
