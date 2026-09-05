using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;
using RocketWiki.Api.Embeddings;
using RocketWiki.Api.Features;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Every feature flag's OFF state on every surface it owns, through the real API
/// (docs/CONFIGURATION.md "Feature flags"; RocketWikiFeatures documents each decision).
/// Three properties are asserted per flag, and they are the whole contract:
/// <list type="bullet">
/// <item>Off is <b>an independent off-switch</b>: with the feature fully CONFIGURED and
/// the flag off, the surface answers exactly what an unconfigured instance answers — no
/// error, no throw, no client, no endpoint. A control host with the same configuration
/// and the flag unset proves the configuration would otherwise have taken effect, so
/// none of these tests can pass vacuously.</item>
/// <item>Off <b>changes nothing about the schema</b>: the all-flags-off host exports the
/// same SDL as the checked-in schema.graphql, byte for byte after line-ending
/// normalization. Fields exist and answer "off"; they never disappear.</item>
/// <item>On <b>turns nothing on</b>: every flag explicitly true on an instance with
/// nothing configured still reads as not-configured.</item>
/// </list>
///
/// <para><b>How the flags reach the host.</b> Three of the six are registration
/// decisions made before <c>Build()</c>, and a WebApplicationFactory's
/// <c>ConfigureAppConfiguration</c> lands after top-level statements have run — the
/// ordering trap ProtectiveMarkingConfiguration documents. <c>UseSetting</c> takes a
/// different route: WebApplicationFactory's deferred host builder turns host settings
/// into <c>--key=value</c> command-line arguments for the entry point, which
/// <c>WebApplication.CreateBuilder(args)</c> reads before the first top-level statement.
/// (It is how <c>UseEnvironment</c> has always reached Program.cs.) Every flag and
/// endpoint below is set that way, so the eager reads see them and the test is real.</para>
/// </summary>
public sealed class FeatureFlagOffStateTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string AssistantConnectionString = "Endpoint=http://127.0.0.1:9/v1;Key=unused;Model=fake-chat";
    private const string EmbeddingsConnectionString = "Endpoint=http://127.0.0.1:9/v1;Key=unused;Model=fake-embed;Dimensions=4";
    private const string GitLabBaseUrl = "https://gitlab.test";

    private WebApplicationFactory<Program> HostWith(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static (string, string) Off(string flag) => ($"FeatureManagement:{flag}", "false");
    private static (string, string) On(string flag) => ($"FeatureManagement:{flag}", "true");

    private static HttpClient UserClient(WebApplicationFactory<Program> host, IEnumerable<string>? roles = null)
    {
        var client = host.CreateClient();
        client.SetTestUser(sub: $"flag-user-{Guid.NewGuid():N}", email: "f@example.test", name: "Flag User", roles: roles);
        return client;
    }

    private static async Task<JsonElement> DataAsync(HttpClient client, string query)
    {
        var response = await client.PostGraphQLAsync(query);
        Assert.False(response.RootElement.TryGetProperty("errors", out var errors),
            $"GraphQL errored where a flag must answer, not throw: {errors}");
        return response.RootElement.GetProperty("data").Clone();
    }

    // ------------------------------------------------------------------ the default

    /// <summary>The most important test here: on the shared fixture — no
    /// FeatureManagement section anywhere — every flag is on, both as the startup snapshot
    /// every surface reads and as the library's own IFeatureManager answers it.</summary>
    [Fact]
    public async Task DefaultConfiguration_EveryFlagIsOn_InTheSnapshotAndInIFeatureManager()
    {
        var snapshot = factory.Services.GetRequiredService<FeatureFlagSnapshot>();
        Assert.Equal(FeatureFlagSnapshot.AllEnabled, snapshot);

        var manager = factory.Services.GetRequiredService<IFeatureManager>();
        foreach (var flag in RocketWikiFeatures.All)
        {
            Assert.True(await manager.IsEnabledAsync(flag), $"IFeatureManager says {flag} is off on a default configuration.");
        }
    }

    /// <summary>The two evaluation paths agree in the other direction too, and the
    /// all-off host still serves the checked-in schema — no field came or went.</summary>
    [Fact]
    public async Task EveryFlagOff_SnapshotAndIFeatureManagerAgree_AndTheSchemaIsUnchanged()
    {
        using var host = HostWith([.. RocketWikiFeatures.All.Select(Off)]);

        var snapshot = host.Services.GetRequiredService<FeatureFlagSnapshot>();
        Assert.Equal(new FeatureFlagSnapshot(false, false, false, false, false, false), snapshot);

        var manager = host.Services.GetRequiredService<IFeatureManager>();
        foreach (var flag in RocketWikiFeatures.All)
        {
            Assert.False(await manager.IsEnabledAsync(flag), $"IFeatureManager says {flag} is on although configuration turned it off.");
        }

        var executor = await host.Services.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var served = Normalize(executor.Schema.ToString());
        var checkedIn = Normalize(await File.ReadAllTextAsync(Path.Combine(RepoRoot.Find(), "schema.graphql")));
        Assert.True(checkedIn == served,
            "With every feature flag off, the served schema differs from schema.graphql — a flag changed the schema's shape, which it must never do.");
    }

    // ------------------------------------------------------------------ AskWiki

    [Fact]
    public async Task AskWikiOff_WithAnAssistantConfigured_AnswersNotConfigured_AndRegistersNoClient()
    {
        using var off = HostWith(("ConnectionStrings:assistant", AssistantConnectionString), Off(RocketWikiFeatures.AskWiki));
        using var control = HostWith(("ConnectionStrings:assistant", AssistantConnectionString));

        // Control: the same configuration with the flag unset registers a client and
        // reports configured — so the flag, not the configuration, is what the off host
        // is showing. The ask itself never reaches the (dead) endpoint on the control:
        // retrieval finds nothing in an empty database and the service stops at NO_RESULTS.
        Assert.NotNull(control.Services.GetService<IChatClient>());
        var controlData = await DataAsync(UserClient(control),
            "{ assistantStatus { configured maxQuestionChars } askWiki(question: \"turbopump\") { unavailable } }");
        Assert.True(controlData.GetProperty("assistantStatus").GetProperty("configured").GetBoolean());
        Assert.Equal(2000, controlData.GetProperty("assistantStatus").GetProperty("maxQuestionChars").GetInt32());
        Assert.Equal("NO_RESULTS", controlData.GetProperty("askWiki").GetProperty("unavailable").GetString());

        Assert.Null(off.Services.GetService<IChatClient>());
        var offData = await DataAsync(UserClient(off),
            "{ assistantStatus { configured maxQuestionChars } askWiki(question: \"turbopump\") { answer citations { pageId } unavailable } }");
        Assert.False(offData.GetProperty("assistantStatus").GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, offData.GetProperty("assistantStatus").GetProperty("maxQuestionChars").ValueKind);
        Assert.Equal("NOT_CONFIGURED", offData.GetProperty("askWiki").GetProperty("unavailable").GetString());
    }

    // ------------------------------------------------------------------ SemanticSearch

    [Fact]
    public async Task SemanticSearchOff_WithEmbeddingsConfigured_RegistersNoGeneratorOrJob_AndSearchStillAnswers()
    {
        using var off = HostWith(("ConnectionStrings:embeddings", EmbeddingsConnectionString), Off(RocketWikiFeatures.SemanticSearch));
        using var control = HostWith(("ConnectionStrings:embeddings", EmbeddingsConnectionString));

        Assert.NotNull(control.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Contains(control.Services.GetServices<IHostedService>(), s => s is EmbeddingBackgroundService);

        Assert.Null(off.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), s => s is EmbeddingBackgroundService);
        Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), s => s is EmbeddingDimensionsStartupCheck);

        // Keyword-only search is the off-state, not an error.
        var data = await DataAsync(UserClient(off), "{ search(query: \"turbopump\") { totalCount edges { node { anchorId } } } }");
        Assert.Equal(0, data.GetProperty("search").GetProperty("totalCount").GetInt32());
    }

    // ------------------------------------------------------------------ GitLab

    [Fact]
    public async Task GitLabOff_WithABaseUrlConfigured_EveryFieldAnswersNotConfigured()
    {
        using var off = HostWith(("GitLab:BaseUrl", GitLabBaseUrl), Off(RocketWikiFeatures.GitLab));
        using var control = HostWith(("GitLab:BaseUrl", GitLabBaseUrl));

        var controlStatus = (await DataAsync(UserClient(control), "{ gitlabStatus { configured baseUrl viewerHasToken } }"))
            .GetProperty("gitlabStatus");
        Assert.True(controlStatus.GetProperty("configured").GetBoolean());
        Assert.Equal(GitLabBaseUrl, controlStatus.GetProperty("baseUrl").GetString());

        var offData = await DataAsync(UserClient(off), """
            {
              gitlabStatus { configured baseUrl viewerHasToken }
              gitlabIssue(projectId: "group/repo", iid: 1) { issue { iid } unavailable { reason } }
              gitlabIssues(projectId: "group/repo") { issues { iid } unavailable { reason } }
              gitlabFile(projectId: "group/repo", path: "README.md") { file { filePath } unavailable { reason } }
            }
            """);
        var offStatus = offData.GetProperty("gitlabStatus");
        Assert.False(offStatus.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, offStatus.GetProperty("baseUrl").ValueKind);
        foreach (var field in new[] { "gitlabIssue", "gitlabIssues", "gitlabFile" })
        {
            Assert.Equal("NOT_CONFIGURED", offData.GetProperty(field).GetProperty("unavailable").GetProperty("reason").GetString());
        }
    }

    // ------------------------------------------------------------------ Mcp

    [Fact]
    public async Task McpOff_TheEndpointAndItsDiscoveryMetadataAreNotMapped()
    {
        using var off = HostWith(Off(RocketWikiFeatures.Mcp));

        // Control: the shared (default) fixture serves the RFC 9728 metadata McpToolTests
        // exercises, so an absence on the off host is the flag's doing.
        var controlMetadata = await factory.CreateClient().GetAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(HttpStatusCode.OK, controlMetadata.StatusCode);

        var offClient = UserClient(off);
        var mcp = await offClient.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.NotFound, mcp.StatusCode);

        var offMetadata = await offClient.GetAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(HttpStatusCode.NotFound, offMetadata.StatusCode);

        // GraphQL on the same host is untouched.
        var data = await DataAsync(offClient, "{ me { isAuthenticated } }");
        Assert.True(data.GetProperty("me").GetProperty("isAuthenticated").GetBoolean());
    }

    // ------------------------------------------------------------------ CoEditing

    [Fact]
    public async Task CoEditingOff_JoinEditSessionAnswersNull_UnauditedLikeNotFound_AndPresenceStillWorks()
    {
        using var off = HostWith(Off(RocketWikiFeatures.CoEditing));
        var page = await SeedEditablePageAsync(off);

        await using var connection = await ConnectHubAsync(off, $"coedit-{Guid.NewGuid():N}");

        var join = await connection.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", page.Id);
        Assert.Null(join);

        // Membership-gated relay: a push from a connection that could not join is the
        // same silent no-op it always was — no exception surfaces to the client.
        await connection.InvokeAsync("PushUpdate", page.Id, new byte[] { 1, 2, 3 });

        // Presence is not flagged and shares the hub: it keeps working.
        await connection.InvokeAsync("JoinPage", page.Id);

        using var scope = off.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var editSessionRows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.SubjectId == page.Id && e.Action.StartsWith("coedit."))
            .CountAsync();
        Assert.Equal(0, editSessionRows);
    }

    // ------------------------------------------------------------------ Sync

    [Fact]
    public async Task SyncOff_SetSpaceExportedRefusesFlagging_AllowsUnflagging_AndSyncStatusReportsDisabled()
    {
        using var off = HostWith(Off(RocketWikiFeatures.Sync));
        var spaceId = await SeedNativeSpaceAsync(off);
        var admin = UserClient(off, roles: ["admin"]);

        var status = (await DataAsync(admin, "{ syncStatus { enabled localInstanceId exportedSpaces { spaceKey } origins { originInstanceId } } }"))
            .GetProperty("syncStatus");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("standalone", status.GetProperty("localInstanceId").GetString());

        var refused = (await DataAsync(admin, $$"""
            mutation { setSpaceExported(input: { spaceId: "{{spaceId}}", exported: true }) { space { id isExported } error { kind message } } }
            """)).GetProperty("setSpaceExported");
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("space").ValueKind);
        Assert.Equal("Validation", refused.GetProperty("error").GetProperty("kind").GetString());
        Assert.Contains("FeatureManagement:Sync", refused.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        // Winding sync down must not require switching it back on.
        var unflagged = (await DataAsync(admin, $$"""
            mutation { setSpaceExported(input: { spaceId: "{{spaceId}}", exported: false }) { space { id isExported } error { kind } } }
            """)).GetProperty("setSpaceExported");
        Assert.Equal(JsonValueKind.Null, unflagged.GetProperty("error").ValueKind);
        Assert.False(unflagged.GetProperty("space").GetProperty("isExported").GetBoolean());

        // Control: the shared fixture reports enabled and lets an admin flag the space.
        var controlSpaceId = await SeedNativeSpaceAsync(factory);
        var controlAdmin = UserClient(factory, roles: ["admin"]);
        Assert.True((await DataAsync(controlAdmin, "{ syncStatus { enabled } }")).GetProperty("syncStatus").GetProperty("enabled").GetBoolean());
        var flagged = (await DataAsync(controlAdmin, $$"""
            mutation { setSpaceExported(input: { spaceId: "{{controlSpaceId}}", exported: true }) { space { isExported } error { kind } } }
            """)).GetProperty("setSpaceExported");
        Assert.True(flagged.GetProperty("space").GetProperty("isExported").GetBoolean());
    }

    // ------------------------------------------------------------------ on + unconfigured

    /// <summary>A flag turns nothing on: every flag explicitly true on an instance with no
    /// endpoint, no GitLab URL, still reads as not-configured everywhere.</summary>
    [Fact]
    public async Task EveryFlagOn_WithNothingConfigured_StillReadsNotConfigured()
    {
        using var host = HostWith([.. RocketWikiFeatures.All.Select(On)]);

        Assert.Equal(FeatureFlagSnapshot.AllEnabled, host.Services.GetRequiredService<FeatureFlagSnapshot>());
        Assert.Null(host.Services.GetService<IChatClient>());
        Assert.Null(host.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>());

        var data = await DataAsync(UserClient(host), """
            {
              assistantStatus { configured }
              askWiki(question: "turbopump") { unavailable }
              gitlabStatus { configured baseUrl }
              gitlabIssue(projectId: "group/repo", iid: 1) { unavailable { reason } }
            }
            """);
        Assert.False(data.GetProperty("assistantStatus").GetProperty("configured").GetBoolean());
        Assert.Equal("NOT_CONFIGURED", data.GetProperty("askWiki").GetProperty("unavailable").GetString());
        Assert.False(data.GetProperty("gitlabStatus").GetProperty("configured").GetBoolean());
        Assert.Equal("NOT_CONFIGURED", data.GetProperty("gitlabIssue").GetProperty("unavailable").GetProperty("reason").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private static string Normalize(string sdl) => sdl.Replace("\r\n", "\n").Trim();

    private static async Task<HubConnection> ConnectHubAsync(WebApplicationFactory<Program> host, string sub)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthHandler.ClaimsHeaderName, TestUserHttpClientExtensions.BuildEncodedClaimsHeaderValue(sub, name: sub));
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>A native space with an everyone-Editor grant and one page: the joiner has
    /// canEdit, so the only thing that can refuse the join is the flag.</summary>
    private static async Task<Page> SeedEditablePageAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = await SeedUserAsync(db);

        var space = new Space
        {
            Key = $"FF{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Flag Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        }));
        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "Flag Page",
            CurrentContent = "# Flag Page", CurrentRevisionNumber = 0,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private static async Task<Guid> SeedNativeSpaceAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = await SeedUserAsync(db);

        var space = new Space
        {
            Key = $"SY{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Sync Flag Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        await db.SaveChangesAsync();
        return space.Id;
    }

    private static async Task<User> SeedUserAsync(RocketWikiDbContext db)
    {
        var user = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
