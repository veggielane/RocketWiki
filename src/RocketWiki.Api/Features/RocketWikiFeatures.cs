namespace RocketWiki.Api.Features;

/// <summary>
/// The feature flags this instance reads (docs/CONFIGURATION.md "Feature flags"): one
/// per optional feature, exactly the six that were already optional by configuration.
/// Each is the name under the <c>FeatureManagement</c> configuration section —
/// <c>FeatureManagement:AskWiki=false</c>, or <c>FeatureManagement__AskWiki=false</c> as
/// an environment variable — and nothing else is flagged: a flag on a feature that has no
/// off-state of its own (audit, authorization, markings) would be a switch with nothing
/// safe on the other side.
///
/// <para><b>A flag is an additional, independent off-switch; it turns nothing on.</b>
/// Every one of these features already turns itself off when its endpoint or credential
/// is absent (§15's fail-closed family). Flag off means off regardless of configuration —
/// a demo can silence the assistant without deleting its connection string. Flag on with
/// nothing configured still means not-configured: a flag alone cannot register a client,
/// map an endpoint, or send content anywhere, because there is nowhere for it to go. The
/// ordering is enforced where each feature registers itself (the flag is checked first,
/// then the configuration), so the two conditions are ANDed rather than either one being
/// able to override the other.</para>
///
/// <para><b>Unset means ON.</b> Microsoft.FeatureManagement's own default for a flag the
/// configuration does not mention is <i>off</i>; <see cref="RocketWikiFeatureDefinitionProvider"/>
/// inverts that for exactly these names, so an upgrade onto this version changes nothing
/// on a default configuration. It also means a flag misspelt in configuration is silently
/// ignored (the feature stays on) rather than silently off — the safer failure for a
/// switch whose purpose is to remove a feature an operator has already configured. The
/// names are pinned against docs/CONFIGURATION.md and the Helm chart by
/// FeatureFlagNameAgreementTests so the documented spelling and the read spelling cannot
/// drift.</para>
///
/// <para><b>Off never changes the GraphQL schema</b> (the GitLab precedent
/// AssistantConfiguration records): every field exists whichever way a flag is set, and
/// a disabled feature answers its existing not-configured shape. See each feature's
/// configuration class for its off-state on every surface.</para>
/// </summary>
public static class RocketWikiFeatures
{
    /// <summary>The configuration section the library and every flag below read.</summary>
    public const string SectionName = "FeatureManagement";

    /// <summary>"Ask the wiki" (design.md §9.5). Off: no chat client or options register,
    /// so <c>askWiki</c> answers <c>NOT_CONFIGURED</c> and <c>assistantStatus</c> reports
    /// <c>configured:false</c> — identical to an instance with no assistant endpoint.</summary>
    public const string AskWiki = "AskWiki";

    /// <summary>Semantic (vector) search and the embedding pipeline (design.md §9.2/§9.3).
    /// Off: no generator, no background job, no startup dimension check register; search
    /// is keyword-only, structurally — identical to an instance with no embeddings
    /// endpoint. No page content travels to the endpoint.</summary>
    public const string SemanticSearch = "SemanticSearch";

    /// <summary>GitLab integration (design.md §18). Off: <c>GitLabOptions.IsConfigured</c>
    /// is false, so every GitLab field answers <c>NOT_CONFIGURED</c> and
    /// <c>gitlabStatus</c> reports <c>configured:false</c> with no base URL — identical to
    /// an instance with no <c>GitLab:BaseUrl</c>. Stored tokens stay stored.</summary>
    public const string GitLab = "GitLab";

    /// <summary>The MCP server (design.md §8). Off: <c>/mcp</c> is not mapped (404, the
    /// HealthEndpoints precedent) and the MCP OAuth discovery scheme is not registered, so
    /// nothing advertises a server that is not there. GraphQL is unaffected.</summary>
    public const string Mcp = "Mcp";

    /// <summary>CRDT co-editing over the notifications hub (design.md §8). Off:
    /// <c>JoinEditSession</c> answers the same silent <c>null</c> every refusal answers,
    /// so the SPA falls back to the solo editor; the other edit-session methods are
    /// membership-gated and therefore no-ops. Presence and notifications are not
    /// flagged and keep working.</summary>
    public const string CoEditing = "CoEditing";

    /// <summary>Low→high sync (design.md §12). Off: <c>setSpaceExported</c> refuses with a
    /// <c>Validation</c> error and <c>syncStatus.enabled</c> reports false. What stays on,
    /// deliberately: the replica read-only invariant (a compliance rule, not a feature),
    /// the outbox journal for spaces already flagged exported (stopping it would fork low
    /// and high the moment the flag came back), and the RocketWiki.Sync CLI, which is a
    /// separate operator tool that reads no API configuration.</summary>
    public const string Sync = "Sync";

    /// <summary>Every flag, for the definition provider's unset-means-on rule and the
    /// name-agreement tests. Order is documentation order.</summary>
    public static readonly IReadOnlyList<string> All = [AskWiki, SemanticSearch, GitLab, Mcp, CoEditing, Sync];
}
