using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The non-Development half of ServiceDefaults' health-endpoint story (Extensions.cs,
/// MapDefaultEndpoints): outside Development the endpoints are gated on an explicit
/// HealthEndpoints:Enabled opt-in, default OFF — fail closed. HealthEndpointTests
/// proves the Development half over the shared fixture; this class layers
/// UseEnvironment("Production") on the same fixture (same SQLite database, same fake
/// auth — kept minimal and honest: it flips ONLY the environment, so Production-only
/// side effects like RequireHttpsMetadata=true exist in the host but are never
/// exercised, since the endpoints under test are anonymous and no request here
/// carries a bearer token through the real JWT handler).
/// </summary>
public sealed class HealthEndpointGateTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task NonDevelopment_WithoutTheOptIn_HealthEndpointsAre404(string path)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var client = host.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task NonDevelopment_WithTheOptIn_HealthEndpointsAre200(string path)
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["HealthEndpoints:Enabled"] = "true" }));
        });
        var client = host.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
