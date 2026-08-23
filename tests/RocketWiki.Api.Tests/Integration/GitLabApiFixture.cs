using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GitLab;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The GitLab-enabled variant of <see cref="RocketWikiApiFactory"/>: layers
/// <c>GitLab:BaseUrl</c> (the feature is otherwise fail-closed absent, by design)
/// and swaps the gitlab named client's primary handler for
/// <see cref="FakeGitLabHandler"/> — registered against the same
/// <see cref="GitLabConfiguration.HttpClientName"/> the production wiring uses, so
/// the real GitLabHttpClient runs over the fake wire. The base (unconfigured)
/// factory stays reachable via <see cref="BaseFactory"/> for NOT_CONFIGURED tests.
/// </summary>
public sealed class GitLabApiFixture : IDisposable
{
    public const string BaseUrl = "https://gitlab.test";

    public RocketWikiApiFactory BaseFactory { get; } = new();

    public FakeGitLabHandler Handler { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }

    public GitLabApiFixture()
    {
        Factory = BaseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitLab:BaseUrl"] = BaseUrl,
                    // Small cap so the size-cap tests don't shuttle megabytes around.
                    ["GitLab:MaxFileBytes"] = "2048",
                });
            });

            builder.ConfigureServices(services =>
            {
                // Runs after GitLabConfiguration's own registration for the same
                // named client, so this primary-handler choice wins; every other
                // configured behavior (timeout, no-resilience, no-logging) stays.
                services.AddHttpClient(GitLabConfiguration.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Handler);
            });
        });
    }

    public void Dispose()
    {
        Factory.Dispose();
        BaseFactory.Dispose();
        Handler.Dispose();
    }
}
