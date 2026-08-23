using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md GitLab integration section, over the real API + GitLabHttpClient with
/// only the wire faked (<see cref="FakeGitLabHandler"/>): happy paths, every typed
/// degradation (§6.7-style absent, never a 500 and never a GraphQL error that eats
/// the page), the size cap, URL encoding, per-resource audit rows, and — the
/// load-bearing one — credential isolation: a fetch runs under the calling user's
/// own token or not at all.
/// </summary>
public sealed class GitLabQueryTests(GitLabApiFixture fixture) : IClassFixture<GitLabApiFixture>
{
    private static string IssueJson(int iid, string title = "Fix the flux capacitor") => $$"""
        {
          "id": 1000, "iid": {{iid}}, "project_id": 7,
          "title": {{JsonSerializer.Serialize(title)}},
          "state": "opened",
          "labels": ["bug", "flight-software"],
          "author": { "name": "Ada Lovelace", "username": "ada" },
          "assignees": [ { "name": "Margaret Hamilton", "username": "mhamilton" } ],
          "web_url": "https://gitlab.test/group/proj/-/issues/{{iid}}",
          "created_at": "2026-01-02T03:04:05.000Z",
          "updated_at": "2026-01-03T04:05:06.000Z",
          "due_date": "2026-02-01",
          "milestone": { "title": "v2.1" },
          "confidential": false
        }
        """;

    private async Task<HttpClient> AuthenticatedClientWithTokenAsync(string sub, string token)
    {
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: sub, email: $"{sub}@example.test", name: sub);

        using var set = await client.PostGraphQLAsync($$"""
            mutation { setGitLabToken(input: { token: "{{token}}" }) { tokenState { hasToken } error { kind message } } }
            """);
        Assert.True(set.RootElement.GetProperty("data").GetProperty("setGitLabToken")
            .GetProperty("tokenState").GetProperty("hasToken").GetBoolean());

        return client;
    }

    [Fact]
    public async Task Issue_HappyPath_ReturnsIssue_UsesCallersToken_AndAuditsTheFetch()
    {
        var sub = $"gl-issue-{Guid.NewGuid():N}";
        var client = await AuthenticatedClientWithTokenAsync(sub, "glpat-happy-path");
        fixture.Handler.RespondWithJson(IssueJson(42));
        var requestsBefore = fixture.Handler.Requests.Count;

        using var result = await client.PostGraphQLAsync("""
            query { gitlabIssue(projectId: "group/proj", iid: 42) {
              issue { project iid title state labels authorName assigneeNames webUrl milestone confidential }
              unavailable { reason upstreamStatus } } }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("gitlabIssue");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("unavailable").ValueKind);
        var issue = payload.GetProperty("issue");
        Assert.Equal("group/proj", issue.GetProperty("project").GetString());
        Assert.Equal(42, issue.GetProperty("iid").GetInt32());
        Assert.Equal("Fix the flux capacitor", issue.GetProperty("title").GetString());
        Assert.Equal("opened", issue.GetProperty("state").GetString());
        Assert.Equal("v2.1", issue.GetProperty("milestone").GetString());
        Assert.Equal("Ada Lovelace", issue.GetProperty("authorName").GetString());

        // The fetch went out under the CALLER's stored token, in GitLab's canonical
        // PAT header — the only place the decrypted token is ever allowed to appear.
        var request = Assert.Single(fixture.Handler.Requests.Skip(requestsBefore));
        Assert.Equal("glpat-happy-path", request.PrivateToken);

        // §7: the fetch is audited as gitlab.fetch with the reference in Details —
        // never the fetched title.
        var audit = await SingleAuditRowAsync(sub, "gitlab.fetch");
        Assert.Contains("group/proj", audit.DetailsJson);
        Assert.Contains("\"outcome\":\"ok\"", audit.DetailsJson);
        Assert.DoesNotContain("flux capacitor", audit.DetailsJson);
    }

    [Fact]
    public async Task Issue_NamespacedProjectPath_TravelsAsOneEscapedPathSegment()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-esc-{Guid.NewGuid():N}", "glpat-escape");
        fixture.Handler.RespondWithJson(IssueJson(7));
        var before = fixture.Handler.Requests.Count;

        using var _ = await client.PostGraphQLAsync("""
            query { gitlabIssue(projectId: "group/subgroup/proj", iid: 7) { issue { iid } unavailable { reason } } }
            """);

        // GitLab's :id is ONE path segment; a raw '/' would address a different
        // route entirely. Also pins .NET's preserve-%2F behavior this client relies on.
        var request = Assert.Single(fixture.Handler.Requests.Skip(before));
        Assert.Contains("/projects/group%2Fsubgroup%2Fproj/issues/7", request.Uri.AbsoluteUri);
        Assert.DoesNotContain("/projects/group/subgroup", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Issues_List_MapsFilterToQueryParameters_AndAuditsResultCount()
    {
        var sub = $"gl-list-{Guid.NewGuid():N}";
        var client = await AuthenticatedClientWithTokenAsync(sub, "glpat-list");
        fixture.Handler.RespondWithJson($"[{IssueJson(1)}, {IssueJson(2, "Second issue")}]");
        var before = fixture.Handler.Requests.Count;

        using var result = await client.PostGraphQLAsync("""
            query { gitlabIssues(projectId: "42",
                filter: { state: "opened", labels: ["bug", "flight-software"], search: "watchdog", milestone: "v2.1", orderBy: "updated_at", sort: "desc" },
                first: 5) {
              issues { iid title } unavailable { reason } } }
            """);

        var payload = result.RootElement.GetProperty("data").GetProperty("gitlabIssues");
        Assert.Equal(2, payload.GetProperty("issues").GetArrayLength());

        var request = Assert.Single(fixture.Handler.Requests.Skip(before));
        var uri = request.Uri.AbsoluteUri;
        Assert.Contains("/projects/42/issues?", uri);
        Assert.Contains("per_page=5", uri);
        Assert.Contains("state=opened", uri);
        Assert.Contains("labels=bug%2Cflight-software", uri);
        Assert.Contains("search=watchdog", uri);
        Assert.Contains("order_by=updated_at", uri);
        Assert.Contains("sort=desc", uri);

        var audit = await SingleAuditRowAsync(sub, "gitlab.fetch");
        Assert.Contains("\"resultCount\":2", audit.DetailsJson);
    }

    [Fact]
    public async Task Issues_OutOfVocabularyFilterValues_DegradeToUnset_NotTo400()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-vocab-{Guid.NewGuid():N}", "glpat-vocab");
        fixture.Handler.RespondWithJson("[]");
        var before = fixture.Handler.Requests.Count;

        using var result = await client.PostGraphQLAsync("""
            query { gitlabIssues(projectId: "42", filter: { state: "definitely-not-a-state", orderBy: "nonsense", sort: "sideways" }) {
              issues { iid } unavailable { reason } } }
            """);

        Assert.Equal(0, result.RootElement.GetProperty("data").GetProperty("gitlabIssues")
            .GetProperty("issues").GetArrayLength());

        // A typo'd fence key degrades to GitLab's own defaults — the list still
        // renders — rather than round-tripping a 400 into a misleading UNREACHABLE.
        var request = Assert.Single(fixture.Handler.Requests.Skip(before));
        Assert.DoesNotContain("state=", request.Uri.AbsoluteUri);
        Assert.DoesNotContain("order_by=", request.Uri.AbsoluteUri);
        Assert.DoesNotContain("sort=", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task File_HappyPath_ReturnsContentAndMetadata()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-file-{Guid.NewGuid():N}", "glpat-file");
        fixture.Handler.RespondWithRawFile(
            Encoding.UTF8.GetBytes("#include <telemetry.h>\n"), "frame.h", "src/telemetry/frame.h", refName: "main");
        var before = fixture.Handler.Requests.Count;

        using var result = await client.PostGraphQLAsync("""
            query { gitlabFile(projectId: "group/proj", path: "src/telemetry/frame.h", ref: "main") {
              file { project filePath ref fileName sizeBytes content contentIssue lastCommitId }
              unavailable { reason } } }
            """);

        var file = result.RootElement.GetProperty("data").GetProperty("gitlabFile").GetProperty("file");
        Assert.Equal("src/telemetry/frame.h", file.GetProperty("filePath").GetString());
        Assert.Equal("frame.h", file.GetProperty("fileName").GetString());
        Assert.Equal("main", file.GetProperty("ref").GetString());
        Assert.Equal("#include <telemetry.h>\n", file.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("contentIssue").ValueKind);

        var request = Assert.Single(fixture.Handler.Requests.Skip(before));
        Assert.Contains("/repository/files/src%2Ftelemetry%2Fframe.h/raw?ref=main", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task File_OverTheCap_ByDeclaredSize_ReturnsMetadataWithoutContent()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-cap-{Guid.NewGuid():N}", "glpat-cap");
        // Declared size over the fixture's 2048-byte cap; tiny actual body proves the
        // short-circuit happened on the header, not after buffering.
        fixture.Handler.RespondWithRawFile(
            Encoding.UTF8.GetBytes("tiny"), "huge.bin", "artifacts/huge.bin", declaredSize: 10_000_000);

        using var result = await client.PostGraphQLAsync("""
            query { gitlabFile(projectId: "42", path: "artifacts/huge.bin") {
              file { sizeBytes content contentIssue } unavailable { reason } } }
            """);

        var file = result.RootElement.GetProperty("data").GetProperty("gitlabFile").GetProperty("file");
        Assert.Equal(10_000_000, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("content").ValueKind);
        Assert.Equal("TOO_LARGE", file.GetProperty("contentIssue").GetString());
    }

    [Fact]
    public async Task File_OverTheCap_ByActualBody_WhenTheSizeHeaderLies_StillCapped()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-liar-{Guid.NewGuid():N}", "glpat-liar");
        fixture.Handler.RespondWithRawFile(
            Encoding.UTF8.GetBytes(new string('x', 5000)), "liar.txt", "liar.txt", declaredSize: 10);

        using var result = await client.PostGraphQLAsync("""
            query { gitlabFile(projectId: "42", path: "liar.txt") {
              file { content contentIssue } unavailable { reason } } }
            """);

        var file = result.RootElement.GetProperty("data").GetProperty("gitlabFile").GetProperty("file");
        Assert.Equal(JsonValueKind.Null, file.GetProperty("content").ValueKind);
        Assert.Equal("TOO_LARGE", file.GetProperty("contentIssue").GetString());
    }

    [Fact]
    public async Task File_BinaryContent_ReturnsMetadataWithNotText()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-bin-{Guid.NewGuid():N}", "glpat-bin");
        fixture.Handler.RespondWithRawFile([0x7F, 0x45, 0x4C, 0x46, 0x00, 0xFF, 0xFE], "probe.elf", "bin/probe.elf");

        using var result = await client.PostGraphQLAsync("""
            query { gitlabFile(projectId: "42", path: "bin/probe.elf") {
              file { fileName content contentIssue } unavailable { reason } } }
            """);

        var file = result.RootElement.GetProperty("data").GetProperty("gitlabFile").GetProperty("file");
        Assert.Equal(JsonValueKind.Null, file.GetProperty("content").ValueKind);
        Assert.Equal("NOT_TEXT", file.GetProperty("contentIssue").GetString());
    }

    [Fact]
    public async Task UnsetBaseUrl_MeansTheFeatureIsAbsent_TypedNotConfigured_NoUpstreamCall()
    {
        // The base factory has no GitLab:BaseUrl — §15 fail-closed: unset = absent.
        var client = fixture.BaseFactory.CreateClient();
        client.SetTestUser(sub: $"gl-unset-{Guid.NewGuid():N}");
        var before = fixture.Handler.Requests.Count;

        using var result = await client.PostGraphQLAsync("""
            query {
              gitlabIssue(projectId: "42", iid: 1) { issue { iid } unavailable { reason upstreamStatus } }
              gitlabStatus { configured baseUrl viewerHasToken }
            }
            """);

        var data = result.RootElement.GetProperty("data");
        var unavailable = data.GetProperty("gitlabIssue").GetProperty("unavailable");
        Assert.Equal("NOT_CONFIGURED", unavailable.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("gitlabIssue").GetProperty("issue").ValueKind);
        Assert.False(data.GetProperty("gitlabStatus").GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("gitlabStatus").GetProperty("baseUrl").ValueKind);
        Assert.Equal(before, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task NoStoredToken_TypedNoCredential_NoUpstreamCall()
    {
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"gl-notoken-{Guid.NewGuid():N}");
        var before = fixture.Handler.Requests.Count;

        using var result = await client.PostGraphQLAsync("""
            query { gitlabFile(projectId: "42", path: "a.txt") { file { content } unavailable { reason } } }
            """);

        Assert.Equal("NO_CREDENTIAL", result.RootElement.GetProperty("data").GetProperty("gitlabFile")
            .GetProperty("unavailable").GetProperty("reason").GetString());
        Assert.Equal(before, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Upstream401_TypedInvalidCredential_SoTheUserLearnsTheirTokenDied()
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-401-{Guid.NewGuid():N}", "glpat-revoked");
        fixture.Handler.RespondWithStatus(HttpStatusCode.Unauthorized);

        using var result = await client.PostGraphQLAsync("""
            query { gitlabIssue(projectId: "42", iid: 1) { issue { iid } unavailable { reason upstreamStatus } } }
            """);

        var unavailable = result.RootElement.GetProperty("data").GetProperty("gitlabIssue").GetProperty("unavailable");
        Assert.Equal("INVALID_CREDENTIAL", unavailable.GetProperty("reason").GetString());
        Assert.Equal(401, unavailable.GetProperty("upstreamStatus").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Upstream403And404_CollapseToNotFound(HttpStatusCode statusCode)
    {
        var client = await AuthenticatedClientWithTokenAsync($"gl-4x-{Guid.NewGuid():N}", "glpat-4x");
        fixture.Handler.RespondWithStatus(statusCode);

        using var result = await client.PostGraphQLAsync("""
            query { gitlabIssue(projectId: "42", iid: 1) { unavailable { reason } } }
            """);

        Assert.Equal("NOT_FOUND", result.RootElement.GetProperty("data").GetProperty("gitlabIssue")
            .GetProperty("unavailable").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task GitLabDown_TypedUnreachable_ThePageAroundTheEmbedSurvives()
    {
        var sub = $"gl-down-{Guid.NewGuid():N}";
        var client = await AuthenticatedClientWithTokenAsync(sub, "glpat-down");
        fixture.Handler.FailWith(new HttpRequestException("connection refused"));

        using var result = await client.PostGraphQLAsync("""
            query {
              gitlabIssue(projectId: "42", iid: 1) { issue { iid } unavailable { reason upstreamStatus } }
              me { isAuthenticated }
            }
            """);

        // No GraphQL "errors" array: the degradation is a typed payload fact, and
        // sibling fields in the same document still resolve.
        Assert.False(result.RootElement.TryGetProperty("errors", out _));
        var unavailable = result.RootElement.GetProperty("data").GetProperty("gitlabIssue").GetProperty("unavailable");
        Assert.Equal("UNREACHABLE", unavailable.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("upstreamStatus").ValueKind);
        Assert.True(result.RootElement.GetProperty("data").GetProperty("me").GetProperty("isAuthenticated").GetBoolean());

        var audit = await SingleAuditRowAsync(sub, "gitlab.fetch");
        Assert.Contains("\"outcome\":\"unreachable\"", audit.DetailsJson);
    }

    [Fact]
    public async Task CredentialIsolation_EachUsersFetchCarriesTheirOwnToken_NeverAnothers()
    {
        var subA = $"gl-userA-{Guid.NewGuid():N}";
        var subB = $"gl-userB-{Guid.NewGuid():N}";
        const string tokenA = "glpat-belongs-to-A-only";
        const string tokenB = "glpat-belongs-to-B-only";

        var clientA = await AuthenticatedClientWithTokenAsync(subA, tokenA);
        var clientB = await AuthenticatedClientWithTokenAsync(subB, tokenB);
        fixture.Handler.RespondWithJson(IssueJson(1));

        var before = fixture.Handler.Requests.Count;
        using var _ = await clientA.PostGraphQLAsync("""query { gitlabIssue(projectId: "42", iid: 1) { unavailable { reason } } }""");
        var requestFromA = Assert.Single(fixture.Handler.Requests.Skip(before));
        Assert.Equal(tokenA, requestFromA.PrivateToken);

        before = fixture.Handler.Requests.Count;
        using var __ = await clientB.PostGraphQLAsync("""query { gitlabIssue(projectId: "42", iid: 1) { unavailable { reason } } }""");
        var requestFromB = Assert.Single(fixture.Handler.Requests.Skip(before));
        Assert.Equal(tokenB, requestFromB.PrivateToken);

        // Adversarial: a third user with NO token gets NO_CREDENTIAL and no request
        // at all — there is no fallback to anyone else's stored token, and no
        // service account to fall back to (none exists anywhere in this system).
        var clientC = fixture.Factory.CreateClient();
        clientC.SetTestUser(sub: $"gl-userC-{Guid.NewGuid():N}");
        before = fixture.Handler.Requests.Count;
        using var result = await clientC.PostGraphQLAsync("""query { gitlabIssue(projectId: "42", iid: 1) { unavailable { reason } } }""");
        Assert.Equal("NO_CREDENTIAL", result.RootElement.GetProperty("data").GetProperty("gitlabIssue")
            .GetProperty("unavailable").GetProperty("reason").GetString());
        Assert.Equal(before, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Audit_DistinctResourcesInOneRequest_AreDistinctRows_SameResourceTwiceIsOne()
    {
        var sub = $"gl-dedup-{Guid.NewGuid():N}";
        var client = await AuthenticatedClientWithTokenAsync(sub, "glpat-dedup");
        fixture.Handler.RespondWith(request =>
        {
            var iid = int.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            return FakeGitLabHandler.JsonResponse(IssueJson(iid));
        });

        using var _ = await client.PostGraphQLAsync("""
            query {
              a: gitlabIssue(projectId: "42", iid: 1) { issue { iid } }
              b: gitlabIssue(projectId: "42", iid: 2) { issue { iid } }
              again: gitlabIssue(projectId: "42", iid: 1) { issue { iid } }
            }
            """);

        var rows = await AuditRowsAsync(sub, "gitlab.fetch");
        // Two distinct resources -> two rows (the DedupKey working); the same
        // resource fetched twice in one request -> one row, like page.view dedup.
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.DetailsJson!.Contains("\"iid\":1"));
        Assert.Single(rows, r => r.DetailsJson!.Contains("\"iid\":2"));
    }

    // --- audit-row helpers ---

    private async Task<Core.Entities.AuditEvent> SingleAuditRowAsync(string sub, string action)
    {
        var rows = await AuditRowsAsync(sub, action);
        return Assert.Single(rows);
    }

    private async Task<List<Core.Entities.AuditEvent>> AuditRowsAsync(string sub, string action)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var userId = await db.Users.Where(u => u.Subject == sub).Select(u => u.Id).SingleAsync();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.Action == action)
            .ToListAsync();
    }
}
