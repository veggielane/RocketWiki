using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The GitLab Settings surface (design.md GitLab integration section): token
/// set/clear round-trip, the never-readable-back guarantee, encryption at rest,
/// and the §7 audit rows (`settings.gitlab_token.set`/`.cleared`) with no token
/// material anywhere.
/// </summary>
public sealed class GitLabTokenMutationTests(GitLabApiFixture fixture) : IClassFixture<GitLabApiFixture>
{
    [Fact]
    public async Task SetThenClear_RoundTrip_StatusFlips_AndBothActionsAudit()
    {
        var sub = $"gl-tok-{Guid.NewGuid():N}";
        const string token = "glpat-round-trip-secret";
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: sub, email: $"{sub}@example.test", name: "Token Tester");

        using var statusBefore = await client.PostGraphQLAsync(
            """query { gitlabStatus { configured baseUrl viewerHasToken } }""");
        var before = statusBefore.RootElement.GetProperty("data").GetProperty("gitlabStatus");
        Assert.True(before.GetProperty("configured").GetBoolean());
        Assert.Equal(GitLabApiFixture.BaseUrl, before.GetProperty("baseUrl").GetString());
        Assert.False(before.GetProperty("viewerHasToken").GetBoolean());

        using var set = await client.PostGraphQLAsync($$"""
            mutation { setGitLabToken(input: { token: "{{token}}" }) { tokenState { hasToken } error { kind } } }
            """);
        Assert.True(set.RootElement.GetProperty("data").GetProperty("setGitLabToken")
            .GetProperty("tokenState").GetProperty("hasToken").GetBoolean());

        using var statusAfterSet = await client.PostGraphQLAsync("""query { gitlabStatus { viewerHasToken } }""");
        Assert.True(statusAfterSet.RootElement.GetProperty("data").GetProperty("gitlabStatus")
            .GetProperty("viewerHasToken").GetBoolean());

        using var clear = await client.PostGraphQLAsync(
            """mutation { clearGitLabToken { tokenState { hasToken } error { kind } } }""");
        Assert.False(clear.RootElement.GetProperty("data").GetProperty("clearGitLabToken")
            .GetProperty("tokenState").GetProperty("hasToken").GetBoolean());

        using var statusAfterClear = await client.PostGraphQLAsync("""query { gitlabStatus { viewerHasToken } }""");
        Assert.False(statusAfterClear.RootElement.GetProperty("data").GetProperty("gitlabStatus")
            .GetProperty("viewerHasToken").GetBoolean());

        // Both mutations audited via the domain-event pipeline, on the graphql channel.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var userId = await db.Users.Where(u => u.Subject == sub).Select(u => u.Id).SingleAsync();
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.Action.StartsWith("settings.gitlab_token"))
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Action == "settings.gitlab_token.set");
        Assert.Single(rows, r => r.Action == "settings.gitlab_token.cleared");
        Assert.All(rows, r =>
        {
            Assert.Equal(AuditOutcome.Success, r.Outcome);
            Assert.Equal(AuditChannel.GraphQl, r.Channel);
        });
    }

    [Fact]
    public async Task TheToken_IsEncryptedAtRest_AndNeverAppearsInAnyAuditRow()
    {
        var sub = $"gl-sec-{Guid.NewGuid():N}";
        const string token = "glpat-ZZNEVERPERSISTPLAINTEXTZZ";
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: sub);

        using var _ = await client.PostGraphQLAsync($$"""
            mutation { setGitLabToken(input: { token: "{{token}}" }) { tokenState { hasToken } error { kind } } }
            """);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var userId = await db.Users.Where(u => u.Subject == sub).Select(u => u.Id).SingleAsync();

        // At rest: the stored value is Data Protection output, not the plaintext —
        // and does not contain it in any recognizable form.
        var stored = await db.GitLabCredentials.AsNoTracking().SingleAsync(c => c.UserId == userId);
        Assert.NotEqual(token, stored.ProtectedToken);
        Assert.DoesNotContain(token, stored.ProtectedToken);
        Assert.DoesNotContain("ZZNEVERPERSISTPLAINTEXTZZ", stored.ProtectedToken);

        // No audit row anywhere carries token material — not this user's rows, not
        // any row (a serializer bug that leaked it into some other row's details
        // would be just as disqualifying).
        var allAudit = await db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.All(allAudit, row =>
            Assert.DoesNotContain("ZZNEVERPERSISTPLAINTEXTZZ",
                $"{row.Action} {row.DetailsJson} {row.SpaceKey} {row.McpClient}"));
    }

    [Fact]
    public async Task TheToken_IsNotReadableBackThroughTheSchema()
    {
        // Structural: no OUTPUT field anywhere in the exported schema is a
        // token-named String — the only String-typed token-shaped field in the whole
        // SDL is the write-only mutation input's. hasToken/viewerHasToken/tokenState
        // are booleans/objects about *whether* one exists, never material. Asserted
        // against the same schema.graphql the drift test pins to code, so a future
        // field that returns token material fails here by name.
        var repoRoot = RepoRoot.Find();
        var sdl = await File.ReadAllTextAsync(Path.Combine(repoRoot, "schema.graphql"));

        var stringTokenFields = System.Text.RegularExpressions.Regex.Matches(
                sdl, @"^\s*(?<name>\w*token\w*)\s*:\s*\[?String",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups["name"].Value)
            .ToList();

        var only = Assert.Single(stringTokenFields);
        Assert.Equal("token", only);

        // ...and that single field is the write-only input, not an output type.
        Assert.Contains("input SetGitLabTokenRequestInput {\n  token: String!",
            sdl.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task SetEmptyToken_IsAValidationError_NotARow()
    {
        var sub = $"gl-empty-{Guid.NewGuid():N}";
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: sub);

        using var result = await client.PostGraphQLAsync(
            """mutation { setGitLabToken(input: { token: "   " }) { tokenState { hasToken } error { kind message } } }""");

        var payload = result.RootElement.GetProperty("data").GetProperty("setGitLabToken");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokenState").ValueKind);
        Assert.Equal("Validation", payload.GetProperty("error").GetProperty("kind").GetString());

        using var status = await client.PostGraphQLAsync("""query { gitlabStatus { viewerHasToken } }""");
        Assert.False(status.RootElement.GetProperty("data").GetProperty("gitlabStatus")
            .GetProperty("viewerHasToken").GetBoolean());
    }

    [Fact]
    public async Task ClearWithNothingStored_IsAValidationError()
    {
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"gl-noclear-{Guid.NewGuid():N}");

        using var result = await client.PostGraphQLAsync(
            """mutation { clearGitLabToken { tokenState { hasToken } error { kind message } } }""");

        var payload = result.RootElement.GetProperty("data").GetProperty("clearGitLabToken");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokenState").ValueKind);
        Assert.Equal("Validation", payload.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AnonymousSet_IsRefusedBeforeTheServiceRuns()
    {
        var client = fixture.Factory.CreateClient(); // no SetTestUser — anonymous

        using var result = await client.PostGraphQLAsync(
            """mutation { setGitLabToken(input: { token: "glpat-anon" }) { tokenState { hasToken } error { kind message } } }""");

        var payload = result.RootElement.GetProperty("data").GetProperty("setGitLabToken");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokenState").ValueKind);
        Assert.Equal("Forbidden", payload.GetProperty("error").GetProperty("kind").GetString());
    }
}
