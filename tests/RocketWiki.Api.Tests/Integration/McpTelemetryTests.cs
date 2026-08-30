using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15 applied to the MCP channel. Two claims:
///
/// 1. The <c>rocketwiki.mcp.*</c> instruments actually emit, tagged with tool name and
///    outcome only — both bounded vocabularies. An unknown tool name (unbounded,
///    client-controlled) collapses to a constant rather than minting a metric series.
/// 2. Nothing content-shaped escapes into telemetry through an MCP tool call: with
///    sentinel strings planted as page content/title, the search query text, and the
///    principal's nationality, every ActivitySource in the process (including the MCP
///    SDK's own <c>Experimental.ModelContextProtocol</c> source) and every RocketWiki
///    meter stays clean. This sweep also covers <see cref="Activity.StatusDescription"/>,
///    which the base TelemetryHygieneTests doesn't: the MCP SDK copies tool error text
///    into span status, so our tools' "not found" errors being content-free constants
///    is load-bearing, and this is the test that keeps it that way.
///
/// In the shared TelemetryTestCollection because ActivitySource/Meter are process-wide
/// (see TelemetryTestCollection's doc).
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class McpTelemetryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string SentinelTitle = "QQMCPSENTINELTITLEQQ";
    private const string SentinelContent = "QQMCPSENTINELCONTENTQQ";
    private const string SentinelSearchText = "QQMCPSENTINELSEARCHQQ";
    private const string SentinelNationality = "QQMCPSENTINELNATIONALITYQQ";

    private static readonly string[] AllSentinels =
        [SentinelTitle, SentinelContent, SentinelSearchText, SentinelNationality];

    private async Task<(Guid PageId, string SpaceKey)> SeedSentinelPageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"MTL{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "MCP Telemetry Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "mcp-telemetry",
            Title = SentinelTitle, CurrentContent = SentinelContent,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();

        // A restriction the caller satisfies, so the rule engine genuinely evaluates
        // the sentinel nationality during the MCP call (same trick as the base
        // TelemetryHygieneTests).
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

        return (page.Id, space.Key);
    }

    private async Task<McpClient> CreateMcpClientAsync(string[] nationality)
    {
        var httpClient = factory.CreateClient();
        httpClient.SetTestUser(sub: $"mcp-tel-{Guid.NewGuid()}", nationality: nationality);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        }, httpClient);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "rocketwiki-telemetry-tests", Version = "1.0.0" },
        });
    }

    [Fact]
    public async Task ToolCalls_IncrementTheMcpCounter_WithBoundedToolAndOutcomeTags()
    {
        var (pageId, _) = await SeedSentinelPageAsync();
        using var collector = new MetricCollector<long>(ApiTelemetry.Meter, "rocketwiki.mcp.tool_calls");

        await using var client = await CreateMcpClientAsync([SentinelNationality]);

        // One success, one deliberate not-found (an error outcome), one call to a tool
        // name this server never registered.
        var ok = await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = pageId.ToString() });
        Assert.NotEqual(true, ok.IsError);

        await client.CallToolAsync("get_page",
            new Dictionary<string, object?> { ["pageId"] = Guid.NewGuid().ToString() });

        try
        {
            await client.CallToolAsync("no_such_tool");
        }
        catch (McpException)
        {
            // Unknown tool surfaces as a protocol error to the client; the server-side
            // measurement below is what this test is about.
        }

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Contains(measurements, m =>
            Equals(m.Tags[ApiTelemetry.McpToolTag], "get_page")
            && Equals(m.Tags[ApiTelemetry.McpOutcomeTag], ApiTelemetry.McpOutcomeSuccess));
        Assert.Contains(measurements, m =>
            Equals(m.Tags[ApiTelemetry.McpToolTag], "get_page")
            && Equals(m.Tags[ApiTelemetry.McpOutcomeTag], ApiTelemetry.McpOutcomeError));
        // The unbounded client-chosen name never becomes a tag value (§15).
        Assert.Contains(measurements, m =>
            Equals(m.Tags[ApiTelemetry.McpToolTag], ApiTelemetry.McpToolUnknown)
            && Equals(m.Tags[ApiTelemetry.McpOutcomeTag], ApiTelemetry.McpOutcomeUnknownTool));
        Assert.DoesNotContain(measurements, m => Equals(m.Tags[ApiTelemetry.McpToolTag], "no_such_tool"));
    }

    [Fact]
    public async Task NoSentinelReachesAnySpanMetricOrStatusDescription_ViaMcpToolCalls()
    {
        var (pageId, spaceKey) = await SeedSentinelPageAsync();

        var captured = new List<Activity>();
        var metricTags = new List<string>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true, // every source, deliberately — incl. the MCP SDK's
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

        await using (var client = await CreateMcpClientAsync([SentinelNationality]))
        {
            // Content sentinel flows OUT through a successful read; search-text
            // sentinel flows IN as a tool argument; the nationality sentinel is
            // evaluated by the rule engine on both calls; and a miss exercises the
            // error path whose message the MCP SDK copies into span status.
            var page = await client.CallToolAsync("get_page",
                new Dictionary<string, object?> { ["pageId"] = pageId.ToString() });
            Assert.NotEqual(true, page.IsError);

            var search = await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = SentinelSearchText, ["spaceKey"] = spaceKey });
            Assert.NotEqual(true, search.IsError);

            await client.CallToolAsync("get_page",
                new Dictionary<string, object?> { ["pageId"] = Guid.NewGuid().ToString() });
        }

        meterListener.Dispose();

        // The MCP session's server side keeps tearing down — and stopping spans — after
        // the client is disposed, so ActivityStopped can still be appending while this
        // test asserts. Enumerate a snapshot taken under the callback's own lock.
        Activity[] capturedSnapshot;
        string[] metricTagsSnapshot;
        lock (captured) { capturedSnapshot = [.. captured]; }
        lock (metricTags) { metricTagsSnapshot = [.. metricTags]; }

        Assert.NotEmpty(capturedSnapshot);
        Assert.NotEmpty(metricTagsSnapshot);

        var violations = new List<string>();
        foreach (var activity in capturedSnapshot)
        {
            Check(violations, $"span display name on source '{activity.Source.Name}'", activity.DisplayName);
            Check(violations, $"span operation name on source '{activity.Source.Name}'", activity.OperationName);
            Check(violations, $"status description on span '{activity.DisplayName}' (source '{activity.Source.Name}')", activity.StatusDescription);

            foreach (var (key, value) in activity.Tags)
            {
                Check(violations, $"tag key on span '{activity.DisplayName}' (source '{activity.Source.Name}')", key);
                Check(violations, $"tag '{key}' on span '{activity.DisplayName}' (source '{activity.Source.Name}')", value);
            }

            foreach (var activityEvent in activity.Events)
            {
                Check(violations, $"event name on span '{activity.DisplayName}'", activityEvent.Name);
                foreach (var (key, value) in activityEvent.Tags)
                {
                    Check(violations, $"event tag '{key}' on span '{activity.DisplayName}'", key);
                    Check(violations, $"event tag '{key}' on span '{activity.DisplayName}'", value?.ToString());
                }
            }

            foreach (var (key, value) in activity.Baggage)
            {
                Check(violations, $"baggage key on span '{activity.DisplayName}'", key);
                Check(violations, $"baggage '{key}' on span '{activity.DisplayName}'", value);
            }
        }

        foreach (var tag in metricTagsSnapshot)
        {
            Check(violations, "metric instrument name or tag", tag);
        }

        Assert.True(violations.Count == 0,
            "design.md §15 forbids page content, search text, and principal attribute values in telemetry — " +
            $"including via the MCP channel. Found {violations.Count} violation(s) across {capturedSnapshot.Length} " +
            "captured activities:\n  " + string.Join("\n  ", violations));
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
