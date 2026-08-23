using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Content;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15 applied to the avatar feature: the Gravatar endpoint's only
/// instrument is a bounded hit/miss/disabled counter, and neither an email nor an
/// email hash may reach a metric tag or a span this system exports. Same sentinel
/// method as <see cref="TelemetryHygieneTests"/>/<see cref="GitLabTelemetryTests"/>.
///
/// <b>The one structural nuance:</b> the hash rides in the URL path, and ASP.NET
/// Core hosting itself stamps <c>url.path</c>-shaped tags onto its request activity
/// whenever any listener asks for all data — which this test's own unfiltered
/// listener does. Production's defense is therefore not redaction but exclusion:
/// ServiceDefaults filters <c>/avatar</c> out of AspNetCore tracing wholesale (like
/// the health endpoints), so the OTel pipeline never records that span at all. That
/// filter is asserted structurally below
/// (<see cref="TheAspNetCoreTracingFilter_ExcludesTheGravatarRoute"/>), and the
/// span sweep exempts exactly the raw hosting activity's URL tags for the gravatar
/// path — tags that exist only because the test listener forces them — while
/// sweeping everything else unexempted.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class AvatarTelemetryTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private const string SentinelEmailLocalPart = "zzsentinelavatarzz";

    [Fact]
    public async Task NoEmailOrHashSentinel_ReachesAMetricTag_OrANonExemptSpan()
    {
        var sentinelEmail = $"{SentinelEmailLocalPart}-{Guid.NewGuid():N}@example.test";
        var missEmail = $"{SentinelEmailLocalPart}-miss-{Guid.NewGuid():N}@example.test";

        // Seed: a real upload under the sentinel email (through the real pipeline -
        // JIT, normalization, hashes).
        var sub = $"av-tel-{Guid.NewGuid():N}";
        var authed = factory.CreateClient();
        authed.SetTestUser(sub: sub, email: sentinelEmail, name: "Telemetry Avatar");
        var upload = await authed.PostAsync("/avatars",
            AvatarEndpointTests.BuildUpload(AvatarEndpointTests.MakePng(512, 512), "a.png", "image/png"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var (hitMd5, hitSha256) = AvatarEmailHasher.Compute(sentinelEmail);
        var (missMd5, missSha256) = AvatarEmailHasher.Compute(missEmail);
        string[] sentinels = [sentinelEmail, missEmail, hitMd5!, hitSha256!, missMd5!, missSha256!];

        var captured = new List<Activity>();
        var metricRecords = new List<string>();
        var outcomeTagValues = new List<string>();

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
            InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument),
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => RecordTags(instrument, tags));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => RecordTags(instrument, tags));
        meterListener.Start();

        void RecordTags(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (metricRecords)
            {
                metricRecords.Add(instrument.Name);
                foreach (var tag in tags)
                {
                    metricRecords.Add($"{tag.Key}={tag.Value}");
                    if (instrument.Name == "rocketwiki.avatars.gravatar_requests"
                        && tag.Key == ApiTelemetry.GravatarOutcomeTag)
                    {
                        outcomeTagValues.Add(tag.Value?.ToString() ?? "");
                    }
                }
            }
        }

        // Exercise all three outcomes: hit + miss on an enabled host, disabled on
        // the default host. All anonymous.
        using (var enabledHost = factory.WithWebHostBuilder(builder =>
                   builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                       new Dictionary<string, string?> { ["Avatars:GravatarEndpointEnabled"] = "true" }))))
        {
            var anonymous = enabledHost.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/avatar/{hitMd5}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/avatar/{hitSha256}?s=64")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/avatar/{missSha256}")).StatusCode);
        }

        var disabledAnonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await disabledAnonymous.GetAsync($"/avatar/{hitMd5}")).StatusCode);

        meterListener.Dispose();

        // Non-vacuous, and the counter's vocabulary is exactly the bounded set.
        Assert.Contains("hit", outcomeTagValues);
        Assert.Contains("miss", outcomeTagValues);
        Assert.Contains("disabled", outcomeTagValues);
        Assert.All(outcomeTagValues, v => Assert.Contains(v, new[] { "hit", "miss", "disabled" }));

        var violations = new List<string>();

        foreach (var record in metricRecords)
        {
            Check(violations, sentinels, "metric instrument name or tag", record);
        }

        foreach (var activity in captured)
        {
            Check(violations, sentinels, $"span '{activity.DisplayName}' name", activity.DisplayName);
            foreach (var (key, value) in activity.Tags)
            {
                if (IsExemptRawHostingUrlTag(activity, key))
                {
                    // Exists only because this test listens unfiltered to the raw
                    // hosting source; production never records this span for
                    // /avatar at all - proven structurally by the filter test below.
                    continue;
                }

                Check(violations, sentinels, $"tag '{key}' on span '{activity.DisplayName}' (source '{activity.Source.Name}')", value);
            }

            foreach (var activityEvent in activity.Events)
            {
                foreach (var (key, value) in activityEvent.Tags)
                {
                    Check(violations, sentinels, $"event tag '{key}' on span '{activity.DisplayName}'", value?.ToString());
                }
            }

            foreach (var (key, value) in activity.Baggage)
            {
                Check(violations, sentinels, $"baggage '{key}' on span '{activity.DisplayName}'", value);
            }
        }

        Assert.True(violations.Count == 0,
            "design.md §15 forbids emails and email hashes in telemetry. " +
            $"Found {violations.Count} violation(s):\n  " + string.Join("\n  ", violations));
    }

    /// <summary>The production defense for the hash-in-url.path leak: the exact
    /// Filter ServiceDefaults registers must exclude the gravatar route — and only
    /// it: the authenticated avatar routes and everything else stay traced.</summary>
    [Fact]
    public void TheAspNetCoreTracingFilter_ExcludesTheGravatarRoute()
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
        Assert.NotNull(options.Filter);

        static HttpContext ContextFor(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            return context;
        }

        Assert.False(options.Filter!(ContextFor($"/avatar/{new string('a', 32)}")));
        Assert.False(options.Filter!(ContextFor($"/avatar/{new string('a', 64)}")));

        Assert.True(options.Filter!(ContextFor("/avatars")));
        Assert.True(options.Filter!(ContextFor($"/users/{Guid.NewGuid()}/avatar")));
        Assert.True(options.Filter!(ContextFor("/graphql")));
    }

    private static bool IsExemptRawHostingUrlTag(Activity activity, string tagKey) =>
        activity.Source.Name is "Microsoft.AspNetCore" or ""
        && tagKey is "url.path" or "url.full" or "http.target" or "url.query" or "http.url";

    private static void Check(List<string> violations, string[] sentinels, string location, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var sentinel in sentinels)
        {
            if (value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{location} contains {sentinel}: '{(value.Length <= 200 ? value : value[..200] + "...")}'");
            }
        }
    }
}
