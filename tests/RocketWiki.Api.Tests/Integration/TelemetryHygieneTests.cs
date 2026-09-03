using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15 "Telemetry is not audit", enforced rather than intended: traces and
/// metrics carry no page content, no search query text, and no principal attribute
/// values. The audit table (§7) is the regulated record of who read what, and telemetry
/// must not become a second, unregulated copy of it.
///
/// Method: seed and then exercise the real API — real Hot Chocolate pipeline, real
/// resolvers, real EF Core, real attachment routes — with distinctive sentinel strings
/// planted in every place §15 names, listen to <b>every</b> ActivitySource and
/// <b>every</b> RocketWiki Meter in the process, and assert no sentinel appears in any
/// span name, tag key, tag value, event, or metric tag.
///
/// The listener deliberately does not filter by source name. A test that only watched
/// the sources this code knows about would miss the case that actually matters: a
/// package upgrade turning on a new span that quietly includes the document text. Every
/// source in the process is in scope, including HotChocolate.Diagnostics,
/// Microsoft.AspNetCore, and SqlClient.
///
/// <para><b>The disclosing surfaces are swept too</b> (design.md §21.8): a page the
/// caller cannot read carries a sentinel selector value and a sentinel caveat country,
/// and its placeholder — through <c>pageAccess</c>, the tree and <c>Page.linkTargets</c>
/// — genuinely renders both in its label (asserted, so the sweep is not vacuous), while
/// the denial reason the gate emits names only the category. If a selector value, a
/// country or a marking label ever reached a span or a metric tag, it lands here.</para>
///
/// <b>Known limit, stated rather than implied:</b> this covers traces and metrics. §15
/// also covers logs, and .NET logging is not interceptable the same way from here — an
/// ILogger message template with a page title in it would not be caught by this test.
/// The rule for logs is enforced by review, not by this test.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryHygieneTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    // Deliberately unlikely strings. Every one of them stands for a category design.md
    // §15 names explicitly, so a failure message says which rule was broken.
    private const string SentinelNationality = "ZZSENTINELNATIONALITYZZ";
    private const string SentinelContent = "ZZSENTINELPAGECONTENTZZ";
    private const string SentinelTitle = "ZZSENTINELPAGETITLEZZ";
    private const string SentinelSearchText = "ZZSENTINELSEARCHTEXTZZ";
    private const string SentinelAttachmentBytes = "ZZSENTINELATTACHMENTZZ";
    private const string SentinelProtectedTitle = "ZZSENTINELPROTECTEDTITLEZZ";

    /// <summary>design.md §21.15/§21.8: a selector VALUE is the marking's content; it
    /// reaches the placeholder label and must reach nothing operational. The category
    /// name (bounded configured vocabulary) is what the denial token carries, and even
    /// that collapses to the single word <c>selector</c> in a metric (§15).</summary>
    private const string SentinelSelector = RocketWikiApiFactory.SentinelSelectorValue;

    private static readonly string[] AllSentinels =
    [
        SentinelNationality, SentinelContent, SentinelTitle, SentinelSearchText, SentinelAttachmentBytes,
        SentinelProtectedTitle, SentinelSelector,
    ];

    private sealed record Fixture(Guid SpaceId, string SpaceKey, Guid PageId, Guid ProtectedPageId);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"seed-{Guid.NewGuid()}",
            DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"TEL{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Telemetry Hygiene Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        }));

        var now = DateTime.UtcNow;

        // A page the caller CANNOT read (design.md §21.8): SECRET, so the clearance gate
        // passes for the SECRET-cleared caller below, and carrying the sentinel selector
        // value in the claim-less SENTINEL category, which no grant confers - so G is the
        // one gate that fails, and the placeholder the caller is shown carries the
        // sentinel selector AND the sentinel country in its label. The readable page
        // below links to it, so Page.linkTargets renders the same placeholder.
        var protectedPage = new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = "telemetry-protected",
            Title = SentinelProtectedTitle,
            CurrentContent = "Nobody reads this.",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Pages.Add(protectedPage);

        var page = new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = "telemetry-hygiene",
            Title = SentinelTitle,
            CurrentContent = $"{SentinelContent} see page://{protectedPage.Id}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Pages.Add(page);

        // design.md §21: a protective marking the caller *is* cleared for, so the
        // clearance gate genuinely evaluates a real level and a real country set on every
        // resolution of this page rather than short-circuiting on the OFFICIAL default.
        // The eyes-only set carries the sentinel nationality, which makes the marking's own
        // contents part of what this sweep is looking for: if a level, a country, or a
        // marking-with-page-id ever reached a span or a metric tag, it lands here. The
        // caveat vocabulary is the fixed five-eyes set (design.md §21.4), so the sentinel
        // token is kept on the row but matches nobody; NZ is what admits the caller, and the
        // sentinel still travels through every label and gate evaluation beside it.
        var marking = new PageMarking { PageId = page.Id, Level = ClassificationLevel.Secret, SetAtUtc = now };
        marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = SentinelNationality });
        marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = "NZ" });
        db.PageMarkings.Add(marking);

        var protectedMarking = new PageMarking { PageId = protectedPage.Id, Level = ClassificationLevel.Secret, SetAtUtc = now };
        protectedMarking.Countries.Add(new PageMarkingCountry { PageId = protectedPage.Id, CountryValue = SentinelNationality });
        protectedMarking.Countries.Add(new PageMarkingCountry { PageId = protectedPage.Id, CountryValue = "NZ" });
        protectedMarking.Selectors.Add(new PageMarkingSelector
        {
            PageId = protectedPage.Id,
            Category = RocketWikiApiFactory.SentinelSelectorCategory,
            Value = SentinelSelector,
        });
        db.PageMarkings.Add(protectedMarking);
        await db.SaveChangesAsync();

        // A restriction the caller *satisfies*, so the rule engine actually evaluates the
        // sentinel nationality against the sentinel value rather than short-circuiting.
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            PageId = page.Id,
            Action = PageAction.View,
            ExpressionJson = RuleExpressionSerializer.Serialize(
                new AttrCondition("nationality", [SentinelNationality])),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });
        await db.SaveChangesAsync();

        return new Fixture(space.Id, space.Key, page.Id, protectedPage.Id);
    }

    [Fact]
    public async Task NoSentinelEverReachesASpanOrAMetricTag()
    {
        var fixture = await SeedAsync();

        var client = factory.CreateClient();
        client.SetTestUser(
            sub: $"tel-{Guid.NewGuid()}",
            email: "telemetry@example.test",
            name: "Telemetry Tester",
            nationality: [SentinelNationality, "NZ"],
            // Cleared for the seeded pages' SECRET markings (design.md §21), so the reads
            // below still succeed and the clearance gate runs on real values.
            clearance: "SECRET");

        var captured = new List<Activity>();
        var metricTags = new List<string>();

        using var activityListener = new ActivityListener
        {
            // Every source in the process, deliberately - see this class's own doc.
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (captured)
                {
                    captured.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith("RocketWiki.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => RecordTags(instrument, tags));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => RecordTags(instrument, tags));
        meterListener.Start();

        void RecordTags(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (metricTags)
            {
                metricTags.Add(instrument.Name);
                foreach (var tag in tags)
                {
                    metricTags.Add($"{tag.Key}={tag.Value}");
                }
            }
        }

        await ExerciseTheApiAsync(client, fixture);

        meterListener.Dispose();

        // Spans can still be stopping (background work, connection teardown) while this
        // test asserts, so enumerate a snapshot taken under the callbacks' own locks —
        // not the live lists.
        Activity[] capturedSnapshot;
        string[] metricTagsSnapshot;
        lock (captured) { capturedSnapshot = [.. captured]; }
        lock (metricTags) { metricTagsSnapshot = [.. metricTags]; }

        // Sanity: if nothing was captured, the assertions below would pass vacuously and
        // this test would be worthless. Both listeners must have seen real traffic.
        Assert.NotEmpty(capturedSnapshot);
        Assert.NotEmpty(metricTagsSnapshot);
        // ... including the permission-check histogram for the selector-denied read,
        // which carries the bounded `selector` category and nothing more (§15).
        Assert.Contains(metricTagsSnapshot, t => t == $"{Core.Telemetry.CoreTelemetry.DenialReasonTag}=selector");

        AssertNoSentinels(capturedSnapshot, metricTagsSnapshot);
    }

    /// <summary>
    /// Every channel a sentinel could plausibly escape through: a GraphQL read that
    /// returns page content, a GraphQL mutation carrying content as an <b>inline
    /// literal</b> in the document (the case §15 calls out — a literal in the query text
    /// isn't covered by suppressing variables), an operation whose name and inline
    /// argument stand in for a search string, the plain-HTTP attachment routes, and the
    /// disclosing surfaces (§21.8) rendering a placeholder whose label carries the
    /// sentinel selector and country.
    /// </summary>
    private static async Task ExerciseTheApiAsync(HttpClient client, Fixture fixture)
    {
        // 1. Read the page - resolves Page.content, evaluates canView against the
        //    sentinel nationality, writes a page.view audit row.
        using var read = await client.PostGraphQLAsync($$"""
            query { page(id: "{{fixture.PageId}}") { id title content } }
            """);
        Assert.Contains(SentinelContent, read.RootElement
            .GetProperty("data").GetProperty("page").GetProperty("content").GetString());

        // 2. Mutation with the content inline in the document, not as a variable.
        using var create = await client.PostGraphQLAsync($$"""
            mutation CreateSentinelPage {
              createPage(input: {
                spaceId: "{{fixture.SpaceId}}"
                slug: "sentinel-child"
                title: "{{SentinelTitle}}"
                content: "{{SentinelContent}}"
              }) { page { id } error { __typename } }
            }
            """);
        Assert.False(create.RootElement.GetProperty("data").GetProperty("createPage")
            .GetProperty("page").ValueKind == JsonValueKind.Null);

        // 3. The real search field (milestone 4), twice - both halves of §15's
        //    "search query text" rule through the genuine path:
        //    3a. The sentinel as the inline query argument. The query text lands in
        //        the search.query audit row's DetailsJson (design.md §7, deliberate -
        //        SearchQueryTests asserts that side) and must never land in a span -
        //        RequestDetails.Document would have leaked exactly this.
        using var search = await client.PostGraphQLAsync($$"""
            query SearchSentinel { search(query: "{{SentinelSearchText}}") { totalCount edges { node { snippet headingPath anchorId } } } }
            """);
        Assert.Equal(0, search.RootElement.GetProperty("data").GetProperty("search")
            .GetProperty("totalCount").GetInt32());

        //    3b. A search that HITS the sentinel page, so sentinel title and content
        //        flow through the full result path - LIKE query, canView, snippet
        //        builder, DataLoader-resolved page { title spaceKey } - while every
        //        span in that pipeline stays clean.
        using var searchHit = await client.PostGraphQLAsync($$"""
            query SearchSentinelContent { search(query: "{{SentinelContent}}") { totalCount edges { node { snippet page { id title spaceKey } } } } }
            """);
        Assert.True(searchHit.RootElement.GetProperty("data").GetProperty("search")
            .GetProperty("totalCount").GetInt32() >= 1);

        //    3c. RQL (design.md §22), both halves: a query that RUNS (the string lands in
        //        the page.query audit row's Details and must not land in a span) and a pure
        //        parse. The sentinel goes in as a bare RQL token so the GraphQL document
        //        carries it as an inline literal, which is the case §15 calls out.
        using var rql = await client.PostGraphQLAsync($$"""
            query RunRql { pageQuery(query: "title ~ {{SentinelSearchText}}") { totalCount errors { code } } }
            """);
        Assert.Equal(0, rql.RootElement.GetProperty("data").GetProperty("pageQuery")
            .GetProperty("totalCount").GetInt32());

        using var rqlHit = await client.PostGraphQLAsync($$"""
            query RunRqlHit { pageQuery(query: "title ~ {{SentinelTitle}}") { totalCount edges { node { page { id title } } } } }
            """);
        Assert.True(rqlHit.RootElement.GetProperty("data").GetProperty("pageQuery")
            .GetProperty("totalCount").GetInt32() >= 1);

        using var rqlParse = await client.PostGraphQLAsync($$"""
            query ParseRqlSentinel { parseRql(query: "title ~ {{SentinelSearchText}}") { isValid canonical } }
            """);
        Assert.True(rqlParse.RootElement.GetProperty("data").GetProperty("parseRql")
            .GetProperty("isValid").GetBoolean());

        // 3d. The tree, which now DISCLOSES the protected page as a placeholder whose
        //     label carries the sentinel selector and country (§21.8). The body must
        //     carry them - that is the non-vacuity of this sweep - and no span may.
        using var tree = await client.PostGraphQLAsync($$"""
            query FindByTitle { pageTree(spaceId: "{{fixture.SpaceId}}") {
              ... on PageTreeNode { id title }
              ... on ProtectedTreeNode { title denial { marking { label } reasons { gate category value } } }
            } }
            """);
        Assert.Equal(JsonValueKind.Undefined, tree.RootElement.TryGetProperty("errors", out var e) ? e.ValueKind : JsonValueKind.Undefined);
        var treeBody = tree.RootElement.ToString();
        Assert.Contains(SentinelSelector, treeBody, StringComparison.Ordinal);
        Assert.Contains(SentinelNationality, treeBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelProtectedTitle, treeBody, StringComparison.Ordinal);

        // 3e. The disclosed read of the protected page itself: denied on the selector
        //     grant, so the placeholder carries the full label.
        using var denied = await client.PostGraphQLAsync($$"""
            query ProtectedRead { pageAccess(id: "{{fixture.ProtectedPageId}}") {
              page { id }
              denial { placeholderTitle noSpaceAccess marking { label } reasons { gate category value countries } }
            } }
            """);
        var access = denied.RootElement.GetProperty("data").GetProperty("pageAccess");
        Assert.Equal(JsonValueKind.Null, access.GetProperty("page").ValueKind);
        var label = access.GetProperty("denial").GetProperty("marking").GetProperty("label").GetString()!;
        Assert.Contains(SentinelSelector, label, StringComparison.Ordinal);
        Assert.Contains(SentinelNationality, label, StringComparison.Ordinal);
        Assert.Equal("SELECTOR_GRANT", access.GetProperty("denial").GetProperty("reasons")[0].GetProperty("gate").GetString());

        // 3f. The readable page's link to the protected one: the same placeholder,
        //     reached through Page.linkTargets.
        using var links = await client.PostGraphQLAsync($$"""
            query Links { pageAccess(id: "{{fixture.PageId}}") { page { id linkTargets { id page { id } denial { marking { label } } } } } }
            """);
        var target = Assert.Single(links.RootElement.GetProperty("data").GetProperty("pageAccess")
            .GetProperty("page").GetProperty("linkTargets").EnumerateArray());
        Assert.Contains(SentinelSelector, target.GetProperty("denial").GetProperty("marking").GetProperty("label").GetString());

        // 3g. MCP get_page_tree: an OMITTING surface (§21.8) - the placeholder, its label
        //     and the sentinel selector never reach the payload; the tool still runs the
        //     same decided walk, so its spans are swept for the same values.
        var mcpBody = await McpToolCallAsync(client, "get_page_tree", "{\"spaceKey\":\"" + fixture.SpaceKey + "\"}");
        Assert.Contains(fixture.PageId.ToString(), mcpBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SentinelSelector, mcpBody, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessDenialView.ProtectedTitle, mcpBody, StringComparison.Ordinal);

        // 3h. A deliberately invalid document containing the search sentinel, so the
        //     GraphQL *error* path is exercised too - a validation error quotes the
        //     offending field name back, and MaxErrorEvents = 0 is what keeps that off
        //     the span. Posted directly rather than through PostGraphQLAsync because Hot
        //     Chocolate answers a validation failure with 400, which that helper rejects.
        var invalidResponse = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""query { {{SentinelSearchText}} }""",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        using var invalid = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
        Assert.True(invalid.RootElement.TryGetProperty("errors", out _));

        // 4. Attachment upload + download: bytes and filename through the plain-HTTP
        //    routes and IFileStorage's own spans.
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(SentinelAttachmentBytes));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", $"{SentinelSearchText}.txt");

        var upload = await client.PostAsync($"/attachments/{fixture.PageId}", form);
        upload.EnsureSuccessStatusCode();
        var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var attachmentId = uploaded.RootElement.GetProperty("id").GetString();

        var download = await client.GetAsync($"/attachments/{attachmentId}");
        download.EnsureSuccessStatusCode();
        Assert.Equal(SentinelAttachmentBytes, await download.Content.ReadAsStringAsync());
    }

    /// <summary>One raw JSON-RPC tools/call against /mcp. The Accept header matters:
    /// without it the server answers a "Not Acceptable" JSON-RPC error before any tool
    /// runs, and the sweep would cover nothing.</summary>
    private static async Task<string> McpToolCallAsync(HttpClient client, string toolName, string argumentsJson)
    {
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName + "\",\"arguments\":" + argumentsJson + "}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"MCP {toolName} answered {(int)response.StatusCode}: {body}");
        return body;
    }

    private static void AssertNoSentinels(IReadOnlyList<Activity> captured, IReadOnlyList<string> metricTags)
    {
        var violations = new List<string>();

        foreach (var activity in captured)
        {
            Check(violations, $"span display name on source '{activity.Source.Name}'", activity.DisplayName);
            Check(violations, $"span operation name on source '{activity.Source.Name}'", activity.OperationName);

            foreach (var (key, value) in activity.Tags)
            {
                Check(violations, $"tag key on span '{activity.DisplayName}' (source '{activity.Source.Name}')", key);
                Check(violations, $"tag '{key}' on span '{activity.DisplayName}' (source '{activity.Source.Name}')", value);
            }

            // Events carry their own tag bags - graphql.error events in particular.
            foreach (var activityEvent in activity.Events)
            {
                Check(violations, $"event name on span '{activity.DisplayName}'", activityEvent.Name);
                foreach (var (key, value) in activityEvent.Tags)
                {
                    Check(violations, $"event tag '{key}' on span '{activity.DisplayName}'", key);
                    Check(violations, $"event tag '{key}' on span '{activity.DisplayName}'", value?.ToString());
                }
            }

            // Baggage propagates across process boundaries and is the easiest thing to
            // pollute accidentally.
            foreach (var (key, value) in activity.Baggage)
            {
                Check(violations, $"baggage key on span '{activity.DisplayName}'", key);
                Check(violations, $"baggage '{key}' on span '{activity.DisplayName}'", value);
            }
        }

        foreach (var tag in metricTags)
        {
            Check(violations, "metric instrument name or tag", tag);
        }

        Assert.True(violations.Count == 0,
            "design.md §15 forbids page content, search text, principal attribute values and marking detail in telemetry. " +
            $"Found {violations.Count} violation(s) across {captured.Count} captured activities:\n  " +
            string.Join("\n  ", violations));
    }

    private static void Check(List<string> violations, string location, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var sentinel in AllSentinels)
        {
            if (value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{location} contains {sentinel}: '{Truncate(value)}'");
            }
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";
}
