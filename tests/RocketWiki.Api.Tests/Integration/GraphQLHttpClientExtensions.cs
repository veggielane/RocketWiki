using System.Net.Http.Json;
using System.Text.Json;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>Minimal POST-and-parse helper for the SQLite integration tier —
/// not a replacement for a real GraphQL client, just enough to keep the
/// resolver-level tests in this project readable.</summary>
public static class GraphQLHttpClientExtensions
{
    public static async Task<JsonDocument> PostGraphQLAsync(this HttpClient client, string query)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}
