using Microsoft.Extensions.DependencyInjection.Extensions;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.GitLab;

/// <summary>
/// Wires the GitLab integration (design.md GitLab integration section): options,
/// encrypted per-user credential storage, and the typed REST v4 client.
///
/// Unlike the embedding pipeline, everything registers even when
/// <c>GitLab:BaseUrl</c> is unset: the schema (and therefore the SPA contract)
/// must not change shape with configuration, so the resolvers exist either way
/// and answer NOT_CONFIGURED themselves. The client is registered but never
/// called in that state (and throws loudly if it ever is — GitLabHttpClient).
///
/// Options are resolved lazily from the container's <see cref="IConfiguration"/>
/// rather than read eagerly off the builder: WebApplicationFactory layers test
/// configuration in during Build(), after this method has already run, and an
/// eager read would bake in "unconfigured" before the test config existed. The
/// production value is identical either way.
/// </summary>
public static class GitLabConfiguration
{
    /// <summary>Named explicitly so tests can re-target this exact client's primary
    /// handler at a fake, and so the name never silently changes with a type rename.</summary>
    public const string HttpClientName = "gitlab";

    public static void AddRocketWikiGitLab(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(sp => GitLabOptions.From(sp.GetRequiredService<IConfiguration>()));

        // Data Protection backs the at-rest encryption of stored tokens. Explicit
        // (although the web host usually registers it) because a missing registration
        // must fail at startup, not at the first token write. Key custody in
        // production is a §15 deployment concern — see DataProtectionTokenProtector.
        builder.Services.AddDataProtection();
        builder.Services.TryAddSingleton<ITokenProtector, DataProtectionTokenProtector>();
        builder.Services.AddScoped<IGitLabCredentialService, GitLabCredentialService>();

        // EXTEXP0001 (RemoveAllResilienceHandlers below) is marked experimental by
        // Microsoft.Extensions.Http.Resilience; accepted deliberately — the
        // alternative (leaving retries on) violates a design rule, and a future
        // rename breaks the build loudly here.
#pragma warning disable EXTEXP0001
        builder.Services.AddHttpClient<IGitLabClient, GitLabHttpClient>(HttpClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<GitLabOptions>();
                // Short timeout; view-time fetches must degrade to placeholders,
                // never hang a page render on a slow GitLab.
                client.Timeout = options.Timeout;
                if (options.IsConfigured)
                {
                    client.BaseAddress = options.ApiBaseUri;
                }
            })
            // design.md §15 (GitLab amendment): the built-in System.Net.Http client
            // span carries url.full — for this client that means repository FILE
            // PATHS and PROJECT PATHS, which §15 forbids in telemetry outright.
            // ActivityHeadersPropagator = null removes the DiagnosticsHandler for
            // this handler chain entirely: no built-in span, and no W3C trace
            // context handed to GitLab either (§15 propagates trace context only to
            // the API's own origin). The bounded rocketwiki.gitlab.* span/metrics
            // (ApiTelemetry) replace it; the built-in http.client.request.duration
            // metric still reports host-level tags, which carry no paths.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ActivityHeadersPropagator = null,
            })
            // The factory's default request logging writes the full request URI at
            // Information level — the same file-path leak §15's log rule forbids,
            // through the one channel TelemetryHygieneTests can't intercept.
            .RemoveAllLoggers()
            // ServiceDefaults applies the standard resilience handler (retries) to
            // every client by default. Deliberately removed here: a page with many
            // embeds retrying against a down GitLab is a retry storm at exactly the
            // wrong moment. One attempt, short timeout, typed UNREACHABLE.
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }
}
