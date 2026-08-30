using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// GraphQL introspection: on in Development, OFF everywhere else (Program.cs).
///
/// <para>Defence in depth, ranked honestly. <c>/graphql</c> is reachable
/// unauthenticated — the pipeline authenticates the PRINCIPAL, not the endpoint — and
/// introspection answered without one. What it hands over is structure, never content: an
/// introspection response cannot name a page, a space or a marking value, and every actual
/// read still returns empty/absent (design.md §6.7). So closing it removes a map, not a
/// disclosure. On an instance whose schema carries field names like <c>clearance</c> and
/// <c>protectiveMarking</c>, a free, unauthenticated, machine-readable inventory of every
/// query, mutation, argument and enum value has no operational purpose in production.
///
/// <para>Both halves are asserted, and the second is the one that keeps this honest: a
/// gate that is off everywhere is not a gate, it is a removed feature — and Nitro is
/// unusable without introspection, so Development must keep it.</para>
///
/// <para>Layered on the shared fixture the same way HealthEndpointGateTests layers the
/// non-Development health gate: it flips ONLY the environment.</para>
/// </summary>
public sealed class IntrospectionGateTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    /// <summary>A minimal introspection query — enough that a server answering it has
    /// introspection on, and small enough not to depend on any schema detail.</summary>
    private const string IntrospectionQuery = "{ __schema { queryType { name } } }";

    /// <summary><c>__typename</c> is NOT introspection and must keep working whatever the
    /// environment: urql's document-cache invalidation in the SPA is keyed on it, so a
    /// change that took it out with introspection would break the frontend's caching
    /// silently rather than loudly.</summary>
    private const string TypenameQuery = "{ __typename }";

    /// <summary>
    /// Posted directly rather than through <c>PostGraphQLAsync</c>: the refusal is a
    /// document-validation error, so it comes back as HTTP 400 and the shared helper's
    /// EnsureSuccessStatusCode would turn a correct refusal into an exception. That the
    /// status is 400 rather than 200-with-errors is itself worth pinning — it is what a
    /// client (and a proxy log) sees.
    /// </summary>
    [Fact]
    public async Task NonDevelopment_IntrospectionIsRefused()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        var client = host.CreateClient();

        using var httpResponse = await client.PostAsJsonAsync("/graphql", new { query = IntrospectionQuery });
        var body = await httpResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, httpResponse.StatusCode);

        using var document = JsonDocument.Parse(body);
        Assert.True(
            document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0,
            $"Expected an introspection refusal outside Development; got: {body}");
        Assert.False(
            document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("__schema", out _),
            "The response still carried a __schema, so introspection answered.");

        // Nothing from the schema leaked into the refusal itself.
        Assert.DoesNotContain("queryType", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_IntrospectionStillAnswers()
    {
        // The fixture's own environment is Development (RocketWikiApiFactory).
        var client = factory.CreateClient();

        using var response = await client.PostGraphQLAsync(IntrospectionQuery);

        Assert.False(response.RootElement.TryGetProperty("errors", out _),
            $"Introspection should answer in Development; got: {response.RootElement}");
        Assert.Equal(
            "Query",
            response.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("queryType")
                .GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task TypenameKeepsWorkingInEveryEnvironment(string environment)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        var client = host.CreateClient();

        using var response = await client.PostGraphQLAsync(TypenameQuery);

        Assert.False(response.RootElement.TryGetProperty("errors", out _),
            $"__typename must not be gated with introspection; got: {response.RootElement}");
        Assert.Equal("Query", response.RootElement.GetProperty("data").GetProperty("__typename").GetString());
    }
}
