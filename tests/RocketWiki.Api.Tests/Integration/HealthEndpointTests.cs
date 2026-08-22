using System.Net;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>ServiceDefaults' health endpoints, exercised over the same SQLite-backed
/// fixture as everything else in this tier (design.md §14).</summary>
public sealed class HealthEndpointTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task Alive_ReturnsHealthy()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        // Exercises the DbContext health check the Aspire SQL Server client
        // integration registers — against SQLite here, which is the point:
        // it proves the check works against *a* provider, not specifically
        // SQL Server connectivity (untestable without a container, design.md §14).
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
