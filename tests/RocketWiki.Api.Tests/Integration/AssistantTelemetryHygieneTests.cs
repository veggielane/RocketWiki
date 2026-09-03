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

    /// <summary>design.md §21.13/§21.8: the answer's aggregate marking is built from its
    /// sources' eyes-only countries and national prefixes, so it is a NEW way for both to
    /// reach a span — and a level in a metric dimension would be a census of the
    /// classified estate, which is the §21.8 reasoning that keeps even the bounded
    /// four-value level out of telemetry. Planted as the retrieved page's own marking, so
    /// the aggregate the ask returns genuinely contains them.</summary>
    private const string SentinelCountry = "ZZSENTINELCOUNTRYZZ";

    private const string SentinelPrefix = "ZZSENTINELPREFIXZZ";

    /// <summary>design.md §21.15/§21.13: a selector value on a retrieved page reaches the
    /// answer's aggregate label (selectors are the union) and each citation's label — a
    /// third marking part with a route to a span, swept like the country and the
    /// prefix. The asker is granted it (below), so the page is genuinely retrieved.</summary>
    private const string SentinelSelector = RocketWikiApiFactory.SentinelSelectorValue;

    private static readonly string[] AllSentinels =
    [
        SentinelQuestion, SentinelContent, SentinelTitle, SentinelHeading, SentinelAnswer,
        SentinelCountry, SentinelPrefix, SentinelSelector,
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
        // The nationality claim admits the seeded page's eyes-only caveat (NZ, from the fixed
        // set - design.md §21.4), so the marking is genuinely evaluated and genuinely
        // aggregated rather than the page simply being filtered out before any of it
        // happens; the sentinel country rides beside it on both sides, matching nobody.
        client.SetTestUser(sub: $"ask-tel-{Guid.NewGuid():N}", nationality: [SentinelCountry, "NZ"]);

        // 1. A successful ask: the sentinel question as an inline literal, sentinel
        //    title/heading/content through retrieval into the model context, the sentinel
        //    answer back out through the GraphQL response path, and the sentinel
        //    country/prefix back out inside the aggregate marking label.
        fixture.ChatClient.Respond = _ => $"{SentinelAnswer} [S1]";
        try
        {
            using var asked = await client.PostGraphQLAsync($$"""
                query {
                  askWiki(question: "{{SentinelQuestion}}") {
                    answer
                    unavailable
                    aggregateMarking { label }
                    citations { title anchorId marking { label } }
                  }
                }
                """);
            var ask = asked.RootElement.GetProperty("data").GetProperty("askWiki");
            Assert.Contains(SentinelAnswer, ask.GetProperty("answer").GetString());

            // Non-vacuous for §21.13 specifically: the aggregate label really does carry
            // both sentinels, so the sweep below has something to find if it leaks.
            var aggregate = ask.GetProperty("aggregateMarking").GetProperty("label").GetString()!;
            Assert.Contains(SentinelCountry, aggregate);
            Assert.Contains(SentinelSelector, aggregate);
            // The aggregate's prefix is the UK toggle on unanimity (design.md §21.12/§21.13),
            // and a legacy sentinel prefix is not UK, so the aggregate renders bare — but each
            // citation's own marking label renders its stored prefix verbatim, which is the
            // surface the sentinel prefix actually reaches and the sweep has to stay clean across.
            var citationLabel = ask.GetProperty("citations").EnumerateArray().Single()
                .GetProperty("marking").GetProperty("label").GetString()!;
            Assert.Contains(SentinelPrefix, citationLabel);
            Assert.Contains(SentinelCountry, citationLabel);

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

        // Spans can still be stopping while this test asserts — enumerate a snapshot
        // taken under the callbacks' own locks, not the live lists.
        Activity[] capturedSnapshot;
        string[] metricTagsSnapshot;
        lock (captured) { capturedSnapshot = [.. captured]; }
        lock (metricTags) { metricTagsSnapshot = [.. metricTags]; }

        Assert.NotEmpty(capturedSnapshot);
        Assert.NotEmpty(metricTagsSnapshot);
        // Non-vacuous for this feature specifically: the assistant instruments fired.
        Assert.Contains(metricTagsSnapshot, t => t.StartsWith("rocketwiki.assistant.", StringComparison.Ordinal));
        Assert.Contains(capturedSnapshot, a => a.OperationName == "rocketwiki.assistant.ask");

        AssertNoSentinels(capturedSnapshot, metricTagsSnapshot);
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
        // The access grant confers the sentinel selector (design.md §21.15), so the asker
        // passes G for the page below and it genuinely enters retrieval and the label.
        var grant = new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        };
        grant.Selectors.Add(new AccessRuleSelector
        {
            Category = RocketWikiApiFactory.SentinelSelectorCategory,
            Value = SentinelSelector,
        });
        db.AccessRules.Add(grant);

        // Sentinels in title, heading text (→ breadcrumb + context header + citation
        // headingPath), and body (→ chunk text → model context). The body mentions the
        // question sentinel so the LIKE-fallback retrieval genuinely hits this page.
        var now = DateTime.UtcNow;
        var page = new Page
        {
            SpaceId = space.Id,
            AncestorPath = "/",
            Slug = "sentinel-ask",
            Title = SentinelTitle,
            CurrentContent = $"# {SentinelHeading}\n\n{SentinelContent} answers {SentinelQuestion} for the sweep.",
            CurrentRevisionNumber = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Pages.Add(page);

        // design.md §21.13: a real marking, at a level the (clearance-less, therefore
        // OFFICIAL-SENSITIVE) asker is admitted to, with a sentinel eyes-only country and a
        // sentinel national prefix. Both flow into the answer's aggregate label and each citation's
        // marking — two new surfaces §15 has to stay clean across.
        var marking = new PageMarking
        {
            PageId = page.Id,
            Level = ClassificationLevel.Official,
            Prefix = SentinelPrefix,
            SetAtUtc = now,
        };
        marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = SentinelCountry });
        marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = "NZ" });
        marking.Selectors.Add(new PageMarkingSelector
        {
            PageId = page.Id,
            Category = RocketWikiApiFactory.SentinelSelectorCategory,
            Value = SentinelSelector,
        });
        db.PageMarkings.Add(marking);

        await db.SaveChangesAsync();
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
            "design.md §15 forbids question text, page content, titles, answers, and marking " +
            "detail (§21.8: a level in a metric dimension is a census of the classified estate) " +
            "in telemetry; the ask-the-wiki pipeline and its aggregate marking are new paths for all of them. " +
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
