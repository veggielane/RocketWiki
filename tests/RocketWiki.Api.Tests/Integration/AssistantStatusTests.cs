using System.Text.Json;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// <c>assistantStatus</c>: the instance configuration the ask surface needs BEFORE
/// anybody asks — is there an assistant, and how long a question will it take.
///
/// <para>The limit crossed the wire for one reason: the SPA's QUESTION_TOO_LONG copy
/// could not name a number, so it named none (<c>unavailableCopy.ts</c> says so
/// explicitly), and the pre-warning character counter was skipped for the same reason. A
/// field on <c>AskWikiPayload</c> would not have helped — it arrives with an answer, i.e.
/// after the ask the counter exists to prevent.</para>
///
/// <para><b>The test that matters is the last one.</b> A status field reporting a limit
/// that differs from the enforced one is worse than no field at all: it would put a
/// confident, wrong number in front of the user and let them write right up to it. So the
/// boundary is driven from what the field reports, in both directions.</para>
/// </summary>
public sealed class AssistantStatusTests(AskWikiApiFixture fixture) : IClassFixture<AskWikiApiFixture>
{
    private const string StatusQuery = "{ assistantStatus { configured maxQuestionChars } }";

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        var response = await client.PostGraphQLAsync(StatusQuery);
        Assert.False(response.RootElement.TryGetProperty("errors", out _),
            $"assistantStatus errored: {response.RootElement}");
        return response.RootElement.GetProperty("data").GetProperty("assistantStatus").Clone();
    }

    private HttpClient CreateUserClient()
    {
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"asker-{Guid.NewGuid():N}", email: "a@example.test", name: "Asker");
        return client;
    }

    [Fact]
    public async Task Configured_ReportsTheInstancesOwnLimit()
    {
        var status = await StatusAsync(CreateUserClient());

        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.Equal(fixture.Options.MaxQuestionChars, status.GetProperty("maxQuestionChars").GetInt32());
    }

    /// <summary>
    /// No assistant, no limit — null rather than the code default. There is no bound when
    /// there is no feature, and a number quoted for an absent assistant is a number about
    /// nothing; the UI's honest state there is the one it already has, no figure at all.
    ///
    /// <para>Mutation-tested: report <c>MaxQuestionChars: 2000</c> instead of null when
    /// options are absent and this fails.</para>
    /// </summary>
    [Fact]
    public async Task NotConfigured_ReportsNoLimitAtAll()
    {
        var client = fixture.BaseFactory.CreateClient();
        client.SetTestUser(sub: $"asker-{Guid.NewGuid():N}");

        var status = await StatusAsync(client);

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("maxQuestionChars").ValueKind);
    }

    /// <summary>
    /// An anonymous caller gets the same answer as an unconfigured instance — deliberately
    /// stricter than the sibling <c>gitlabStatus</c>, which this does not copy.
    /// <c>askWiki</c> refuses anonymous callers outright, so a status field that answered
    /// them would be describing a feature they cannot reach; and this instance closes
    /// introspection outside Development on the argument that an unauthenticated,
    /// machine-readable inventory of what a classified system runs has no operational
    /// purpose. "There is an AI assistant here" is a small entry in that inventory.
    ///
    /// <para>Mutation-tested: drop the <c>principalAccessor.Current is null</c> guard and
    /// this fails, reporting configured:true to an anonymous caller.</para>
    /// </summary>
    [Fact]
    public async Task Anonymous_GetsTheAbsentShape_EvenOnAConfiguredInstance()
    {
        var client = fixture.Factory.CreateClient(); // no test-user header

        var status = await StatusAsync(client);

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("maxQuestionChars").ValueKind);
    }

    /// <summary>
    /// The reported limit IS the enforced limit, checked on both sides of the boundary.
    ///
    /// <para>This is the whole point of shipping the number. A field that reported, say,
    /// the code default while the instance enforced something lower would let the SPA draw
    /// a counter that goes green right up to a length the server refuses — a worse failure
    /// than today's no-number copy, because it would be confidently wrong.</para>
    ///
    /// <para>Mutation-tested: report <c>MaxQuestionChars + 1</c> and the
    /// exactly-at-the-limit case starts coming back QUESTION_TOO_LONG.</para>
    /// </summary>
    [Fact]
    public async Task TheReportedLimitIsTheEnforcedLimit()
    {
        var client = CreateUserClient();
        var limit = (await StatusAsync(client)).GetProperty("maxQuestionChars").GetInt32();

        // One over: refused, by the number the field just reported.
        var over = await AskAsync(client, new string('q', limit + 1));
        Assert.Equal("QUESTION_TOO_LONG", over.GetProperty("unavailable").GetString());

        // Exactly at it: NOT refused for length. What it answers instead is not this
        // test's business (retrieval may legitimately find nothing) — only that the
        // length gate did not fire one character below where the field said it would.
        var atLimit = await AskAsync(client, new string('q', limit));
        var unavailable = atLimit.GetProperty("unavailable");
        Assert.True(
            unavailable.ValueKind == JsonValueKind.Null || unavailable.GetString() != "QUESTION_TOO_LONG",
            $"a question of exactly the reported limit ({limit}) was refused as too long.");
    }

    private static async Task<JsonElement> AskAsync(HttpClient client, string question)
    {
        var response = await client.PostGraphQLAsync($$"""
            query { askWiki(question: "{{question}}") { answer unavailable } }
            """);
        Assert.False(response.RootElement.TryGetProperty("errors", out _),
            $"askWiki errored: {response.RootElement}");
        return response.RootElement.GetProperty("data").GetProperty("askWiki").Clone();
    }
}
