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
/// TelemetryHygieneTests' §15 rule extended over the ask-the-wiki paths: an ask
/// carries the question (search text by another name) through retrieval, places page
/// content, titles and heading text into the model context, and brings a model
/// answer back through the GraphQL pipeline — every one of them §15-forbidden in
/// telemetry, every one sentinel-planted here. Same method as the base class: an
/// unfiltered ActivityListener plus a RocketWiki.* MeterListener around the REAL
/// pipeline (fake chat endpoint only, real SearchService/PageReadService/chunker),
/// and a failed ask too, so the unreachable disposition path is swept as well.
/// The rocketwiki.assistant.* instruments must fire (non-vacuous) and carry only
/// the bounded disposition vocabulary.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class AssistantTelemetryHygieneTests(AskWikiApiFixture fixture) : IClassFixture<AskWikiApiFixture>
{
    private const string SentinelQuestion = "ZZSENTINELASKQUESTIONZZ";
    private const string SentinelContent = "ZZSENTINELPAGECONTENTZZ";
    private const string SentinelTitle = "ZZSENTINELPAGETITLEZZ";
    private const string SentinelHeading = "ZZSENTINELHEADINGZZ";
    private const string SentinelAnswer = "ZZSENTINELMODELANSWERZZ";

    private static readonly string[] AllSentinels =
    [
        SentinelQuestion, SentinelContent, SentinelTitle, SentinelHeading, SentinelAnswer,
    ];

    [Fact]
    public async Task AskWiki_QuestionContextAndAnswer_LeakNoSentinelIntoTelemetry()
    {
        fixture.ChatClient.Reset();
        await SeedSentinelPageAsync();

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

        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"ask-tel-{Guid.NewGuid():N}");

        // 1. A successful ask: the sentinel question as an inline literal, sentinel
        //    title/heading/content through retrieval into the model context, and the
        //    sentinel answer back out through the GraphQL response path.
        fixture.ChatClient.Respond = _ => $"{SentinelAnswer} [S1]";
        try
        {
            using var asked = await client.PostGraphQLAsync($$"""
                query { askWiki(question: "{{SentinelQuestion}}") { answer citations { title anchorId } unavailable } }
                """);
            var ask = asked.RootElement.GetProperty("data").GetProperty("askWiki");
            Assert.Contains(SentinelAnswer, ask.GetProperty("answer").GetString());

            // 2. The unreachable path: content already traveled, then the endpoint
            //    fails — the degraded disposition must be exactly as clean.
            fixture.ChatClient.ThrowOnCall = new HttpRequestException("down");
            using var failed = await client.PostGraphQLAsync($$"""
                query { askWiki(question: "{{SentinelQuestion}}") { unavailable } }
                """);
            Assert.Equal("UNREACHABLE", failed.RootElement.GetProperty("data").GetProperty("askWiki")
                .GetProperty("unavailable").GetString());
        }
        finally
        {
            fixture.ChatClient.Reset();
        }

        meterListener.Dispose();

        Assert.NotEmpty(captured);
        Assert.NotEmpty(metricTags);
        // Non-vacuous for this feature specifically: the assistant instruments fired.
        Assert.Contains(metricTags, t => t.StartsWith("rocketwiki.assistant.", StringComparison.Ordinal));
        Assert.Contains(captured, a => a.OperationName == "rocketwiki.assistant.ask");

        AssertNoSentinels(captured, metricTags);
    }

    private async Task SeedSentinelPageAsync()
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
            Key = $"AT{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Assistant Telemetry Space",
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

        // Sentinels in title, heading text (→ breadcrumb + context header + citation
        // headingPath), and body (→ chunk text → model context). The body mentions the
        // question sentinel so the LIKE-fallback retrieval genuinely hits this page.
        var now = DateTime.UtcNow;
        db.Pages.Add(new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = "sentinel-ask",
            Title = SentinelTitle,
            CurrentContent = $"# {SentinelHeading}\n\n{SentinelContent} answers {SentinelQuestion} for the sweep.",
            CurrentRevisionNumber = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
    }

    private static void AssertNoSentinels(List<Activity> captured, List<string> metricTags)
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
            "design.md §15 forbids question text, page content, titles, and answers in telemetry; " +
            "the ask-the-wiki pipeline is a new path for all of them. " +
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
