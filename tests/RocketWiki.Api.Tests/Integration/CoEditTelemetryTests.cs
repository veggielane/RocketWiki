using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15 applied to the co-editing relay, TelemetryHygieneTests' method on the
/// new content-bearing path: CRDT update bytes ARE page content (one §15 tier stricter
/// than pointer coordinates — the §8 co-editing note), so a sentinel is pushed through
/// the real hub as an update, an awareness payload, and a join/replay, while every
/// ActivitySource and every RocketWiki meter in the process is watched. Checked in two
/// encodings — the raw UTF-8 sentinel and the payload's Base64 (what a byte[] becomes
/// if something stringifies it into a tag) — because "we only tag sizes" must hold
/// against both accidents. The boundedness half then asserts the coedit instruments
/// carry only their fixed vocabularies (§15: sizes and counts are bounded facts;
/// mirror of storage's byte counters).
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class CoEditTelemetryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string SentinelUpdateText = "ZZSENTINELCRDTUPDATEZZ";
    private const string SentinelAwarenessText = "ZZSENTINELCARETZZ";

    private async Task<HubConnection> ConnectAsync(string sub)
    {
        var claimsHeader = TestUserHttpClientExtensions.BuildEncodedClaimsHeaderValue(sub, name: sub);
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthHandler.ClaimsHeaderName, claimsHeader);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private async Task<Guid> SeedPageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var seeder = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"CT{Guid.NewGuid():N}"[..8].ToUpperInvariant(), Name = "CoEdit Telemetry Space", OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant, SpaceId = space.Id, Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = seeder.Id, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = seeder.Id,
        });
        var page = new Page
        {
            SpaceId = space.Id, AncestorPath = "/", Slug = "p", Title = "CoEdit Telemetry Page",
            CurrentContent = "# v0", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    [Fact]
    public async Task RelayedContent_NeverReachesTelemetry_AndCoEditTagsStayBounded()
    {
        var pageId = await SeedPageAsync();

        var updateBytes = Encoding.UTF8.GetBytes(SentinelUpdateText);
        var awarenessBytes = Encoding.UTF8.GetBytes(SentinelAwarenessText);
        string[] forbidden =
        [
            SentinelUpdateText,
            SentinelAwarenessText,
            Convert.ToBase64String(updateBytes),
            Convert.ToBase64String(awarenessBytes),
        ];

        var captured = new List<Activity>();
        var metricTags = new List<string>();
        var coEditTagValues = new Dictionary<string, HashSet<string>>();
        long relayMeasurements = 0;

        using var activityListener = new ActivityListener
        {
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
                if (instrument.Name == "rocketwiki.coedit.relay_bytes")
                {
                    relayMeasurements++;
                }

                foreach (var tag in tags)
                {
                    metricTags.Add($"{tag.Key}={tag.Value}");
                    if (tag.Key.StartsWith("rocketwiki.coedit.", StringComparison.Ordinal))
                    {
                        if (!coEditTagValues.TryGetValue(tag.Key, out var values))
                        {
                            coEditTagValues[tag.Key] = values = [];
                        }

                        values.Add(tag.Value?.ToString() ?? "<null>");
                    }
                }
            }
        }

        // The whole relay surface: join (audits + counts), sentinel update (relayed +
        // logged), sentinel awareness (relayed, never logged), late-join replay of the
        // sentinel-bearing log, and explicit leave.
        await using (var alice = await ConnectAsync($"alice-{Guid.NewGuid()}"))
        await using (var bob = await ConnectAsync($"bob-{Guid.NewGuid()}"))
        {
            var bobGot = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            bob.On<Guid, byte[]>("UpdateReceived", (_, u) => bobGot.TrySetResult(u));

            Assert.NotNull(await alice.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", pageId));
            Assert.NotNull(await bob.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", pageId));
            await alice.InvokeAsync("PushUpdate", pageId, updateBytes);
            await alice.InvokeAsync("PushAwareness", pageId, awarenessBytes);
            Assert.Equal(updateBytes, await bobGot.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            await using var carol = await ConnectAsync($"carol-{Guid.NewGuid()}");
            var replay = await carol.InvokeAsync<EditSessionJoinResult?>("JoinEditSession", pageId);
            Assert.Equal(new[] { updateBytes }, replay!.UpdateLog); // sentinel really flowed

            await alice.InvokeAsync("LeaveEditSession", pageId);
        }

        meterListener.Dispose();

        // Spans can still be stopping (SignalR connection teardown) while this test
        // asserts — enumerate snapshots taken under the callbacks' own locks.
        Activity[] capturedSnapshot;
        string[] metricTagsSnapshot;
        Dictionary<string, string[]> coEditTagValuesSnapshot;
        long relayMeasurementsSnapshot;
        lock (captured) { capturedSnapshot = [.. captured]; }
        lock (metricTags)
        {
            metricTagsSnapshot = [.. metricTags];
            coEditTagValuesSnapshot = coEditTagValues.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
            relayMeasurementsSnapshot = relayMeasurements;
        }

        Assert.NotEmpty(capturedSnapshot);
        Assert.NotEmpty(metricTagsSnapshot);
        Assert.True(relayMeasurementsSnapshot >= 2, "relay_bytes must have measured the update and the awareness payload");

        // §15 half: no sentinel, in either encoding, anywhere in trace or metric data.
        var violations = new List<string>();
        foreach (var activity in capturedSnapshot)
        {
            Check(violations, forbidden, $"span '{activity.DisplayName}' (source '{activity.Source.Name}')", activity.DisplayName);
            Check(violations, forbidden, $"span operation '{activity.OperationName}'", activity.OperationName);
            foreach (var (key, value) in activity.Tags)
            {
                Check(violations, forbidden, $"tag key on span '{activity.DisplayName}'", key);
                Check(violations, forbidden, $"tag '{key}' on span '{activity.DisplayName}'", value);
            }

            foreach (var activityEvent in activity.Events)
            {
                Check(violations, forbidden, $"event on span '{activity.DisplayName}'", activityEvent.Name);
                foreach (var (key, value) in activityEvent.Tags)
                {
                    Check(violations, forbidden, $"event tag '{key}' on span '{activity.DisplayName}'", value?.ToString());
                }
            }

            foreach (var (key, value) in activity.Baggage)
            {
                Check(violations, forbidden, $"baggage '{key}' on span '{activity.DisplayName}'", value);
            }
        }

        foreach (var tag in metricTagsSnapshot)
        {
            Check(violations, forbidden, "metric instrument name or tag", tag);
        }

        Assert.True(violations.Count == 0,
            "design.md §15/§8: CRDT update bytes are page content and must never reach telemetry in any encoding. " +
            $"Found {violations.Count} violation(s):\n  " + string.Join("\n  ", violations));

        // Boundedness half: every rocketwiki.coedit.* tag value observed is from the
        // fixed vocabularies - no id, no free text, no size-as-tag.
        var allowed = new Dictionary<string, string[]>
        {
            ["rocketwiki.coedit.kind"] = ["update", "awareness", "oversized"],
            ["rocketwiki.coedit.outcome"] = ["joined", "no_principal", "no_local_user", "not_found", "denied"],
            ["rocketwiki.coedit.reset_reason"] = ["cap_reseed", "expired"],
        };
        foreach (var (tagKey, values) in coEditTagValuesSnapshot)
        {
            Assert.True(allowed.TryGetValue(tagKey, out var allowedValues),
                $"Unexpected coedit metric tag '{tagKey}' - extend the bounded vocabulary deliberately or remove the tag.");
            foreach (var value in values)
            {
                Assert.Contains(value, allowedValues);
            }
        }
    }

    private static void Check(List<string> violations, string[] forbidden, string location, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var sentinel in forbidden)
        {
            if (value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{location} contains forbidden content: '{value[..Math.Min(value.Length, 200)]}'");
            }
        }
    }
}
