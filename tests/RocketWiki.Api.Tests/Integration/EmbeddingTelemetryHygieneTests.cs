using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// TelemetryHygieneTests' §15 rule extended over milestone 7's new content-bearing
/// paths: the embedding indexer chunks and hashes raw page content (including heading
/// TEXT, which flows into breadcrumbs and embedding inputs), and hybrid search embeds
/// the raw QUERY text — both squarely "page content / search query text" that must
/// never reach a span name, tag, event, or metric tag. Same method as the base class:
/// sentinels planted in content and query, an unfiltered ActivityListener plus a
/// RocketWiki.* MeterListener, and the REAL pipeline (fake endpoint only) driven
/// through both the background-job path and the GraphQL search path.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class EmbeddingTelemetryHygieneTests(EmbeddingApiFixture fixture) : IClassFixture<EmbeddingApiFixture>
{
    private const string SentinelContent = "ZZSENTINELPAGECONTENTZZ";
    private const string SentinelHeading = "ZZSENTINELHEADINGZZ";
    private const string SentinelSearchText = "ZZSENTINELSEARCHTEXTZZ";

    private static readonly string[] AllSentinels = [SentinelContent, SentinelHeading, SentinelSearchText];

    [Fact]
    public async Task EmbeddingJobAndHybridSearch_LeakNoSentinelIntoTelemetry()
    {
        var spaceKey = await SeedSentinelPageAsync();

        var captured = new List<Activity>();
        var metricTags = new List<string>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true, // every source in the process, same as the base hygiene test
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

        // 1. The background-job path: sentinel content through chunker, hasher, fake
        //    endpoint, and store — the rocketwiki.embeddings.* span and counters fire here.
        var run = await fixture.RunIndexerAsync();
        Assert.True(run.PagesEmbedded > 0);

        // 2. The hybrid search path: the sentinel as the raw query (semantic-only —
        //    embedded, scanned, RRF-fused) and as a keyword+vector hit, both through the
        //    real GraphQL pipeline.
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"emb-tel-{Guid.NewGuid():N}");
        using var semantic = await client.PostGraphQLAsync($$"""
            query EmbHygiene1 { search(query: "{{SentinelSearchText}}", spaceKey: "{{spaceKey}}") { totalCount edges { node { snippet headingPath anchorId } } } }
            """);
        using var hit = await client.PostGraphQLAsync($$"""
            query EmbHygiene2 { search(query: "{{SentinelContent}}", spaceKey: "{{spaceKey}}") { totalCount edges { node { snippet headingPath anchorId page { id title } } } } }
            """);
        Assert.True(hit.RootElement.GetProperty("data").GetProperty("search").GetProperty("totalCount").GetInt32() >= 1);

        meterListener.Dispose();

        // Spans can still be stopping while this test asserts — enumerate a snapshot
        // taken under the callbacks' own locks, not the live lists.
        Activity[] capturedSnapshot;
        string[] metricTagsSnapshot;
        lock (captured) { capturedSnapshot = [.. captured]; }
        lock (metricTags) { metricTagsSnapshot = [.. metricTags]; }

        Assert.NotEmpty(capturedSnapshot);
        Assert.NotEmpty(metricTagsSnapshot);
        Assert.Contains(metricTagsSnapshot, t => t.StartsWith("rocketwiki.embeddings.", StringComparison.Ordinal));

        AssertNoSentinels(capturedSnapshot, metricTagsSnapshot);
    }

    private async Task<string> SeedSentinelPageAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User
        {
            Subject = $"seed-{Guid.NewGuid():N}",
            DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(creator);

        var space = new Space
        {
            Key = $"ET{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Embedding Telemetry Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        // Sentinels in both heading text (→ breadcrumb, anchor input, HeadingPath
        // column, embedding input) and body (→ chunk text, hash input, snippet).
        var now = DateTime.UtcNow;
        db.Pages.Add(new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = "sentinel-embedding",
            Title = "Sentinel Embedding Page",
            CurrentContent = $"# {SentinelHeading}\n\n{SentinelContent} body text for the hygiene sweep",
            CurrentRevisionNumber = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
        return space.Key;
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

        foreach (var tag in metricTags)
        {
            Check(violations, "metric instrument name or tag", tag);
        }

        Assert.True(violations.Count == 0,
            "design.md §15 forbids page content and search text in telemetry; the embedding pipeline is a new path for both. " +
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
                violations.Add($"{location} contains {sentinel}: '{value[..Math.Min(value.Length, 200)]}'");
            }
        }
    }
}
