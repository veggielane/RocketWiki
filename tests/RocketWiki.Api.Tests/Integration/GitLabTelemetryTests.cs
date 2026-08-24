using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GitLab;
using RocketWiki.Api.Telemetry;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15's GitLab amendment, enforced: telemetry for GitLab fetches carries
/// bounded tags only (operation, outcome, status class) — never a project path, file
/// path, issue title, or filter text. Sentinels flow through the fake GitLab into
/// the full pipeline (resolver → GitLabHttpClient → response mapping → GraphQL
/// serialization) while every ActivitySource and every meter in the process is
/// under watch, the same method as <see cref="TelemetryHygieneTests"/>.
///
/// <b>Known limit, stated:</b> the fake replaces the primary handler, so the
/// built-in System.Net.Http span (whose url.full would carry the sentinel file
/// path) does not exist in this tier regardless of configuration. The production
/// defense — no DiagnosticsHandler on the gitlab client at all — is therefore
/// asserted structurally by <see cref="TheRealGitLabClient_HasNoActivityPropagator"/>
/// against the unfaked production handler chain.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class GitLabTelemetryTests(GitLabApiFixture fixture) : IClassFixture<GitLabApiFixture>
{
    private const string SentinelTitle = "ZZSENTINELGITLABTITLEZZ";
    private const string SentinelPath = "ZZSENTINELGITLABPATHZZ";
    private const string SentinelContent = "ZZSENTINELGITLABCONTENTZZ";
    private const string SentinelSearch = "ZZSENTINELGITLABSEARCHZZ";

    private static readonly string[] AllSentinels = [SentinelTitle, SentinelPath, SentinelContent, SentinelSearch];

    [Fact]
    public async Task NoGitLabSentinelEverReachesASpanOrAMetricTag()
    {
        var client = fixture.Factory.CreateClient();
        client.SetTestUser(sub: $"gl-tel-{Guid.NewGuid():N}");
        using var set = await client.PostGraphQLAsync(
            """mutation { setGitLabToken(input: { token: "glpat-telemetry" }) { tokenState { hasToken } error { kind } } }""");

        var captured = new List<Activity>();
        var metricTags = new List<string>();

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
            lock (metricTags)
            {
                metricTags.Add(instrument.Name);
                foreach (var tag in tags)
                {
                    metricTags.Add($"{tag.Key}={tag.Value}");
                }
            }
        }

        // Sentinel issue title through the issue path, sentinel filter text through
        // the list path, sentinel file path AND content through the file path. All
        // three return their sentinels in the GraphQL response (proof they flowed);
        // none may reach a span or metric.
        fixture.Handler.RespondWithJson($$"""
            { "iid": 9, "title": "{{SentinelTitle}}", "state": "opened", "labels": [],
              "author": null, "assignees": [], "web_url": "https://gitlab.test/x/-/issues/9",
              "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z",
              "due_date": null, "milestone": null, "confidential": false }
            """);
        using var issue = await client.PostGraphQLAsync(
            """query { gitlabIssue(projectId: "42", iid: 9) { issue { title } unavailable { reason } } }""");
        Assert.Equal(SentinelTitle, issue.RootElement.GetProperty("data").GetProperty("gitlabIssue")
            .GetProperty("issue").GetProperty("title").GetString());

        using var list = await client.PostGraphQLAsync($$"""
            query { gitlabIssues(projectId: "42", filter: { search: "{{SentinelSearch}}" }) { issues { iid } unavailable { reason } } }
            """);

        fixture.Handler.RespondWithRawFile(
            Encoding.UTF8.GetBytes(SentinelContent), $"{SentinelPath}.h", $"src/{SentinelPath}.h");
        using var file = await client.PostGraphQLAsync($$"""
            query { gitlabFile(projectId: "42", path: "src/{{SentinelPath}}.h") { file { content } unavailable { reason } } }
            """);
        Assert.Equal(SentinelContent, file.RootElement.GetProperty("data").GetProperty("gitlabFile")
            .GetProperty("file").GetProperty("content").GetString());

        meterListener.Dispose();

        // Spans can still be stopping while this test asserts — enumerate a snapshot
        // taken under the callbacks' own locks, not the live lists.
        Activity[] capturedSnapshot;
        string[] metricTagsSnapshot;
        lock (captured) { capturedSnapshot = [.. captured]; }
        lock (metricTags) { metricTagsSnapshot = [.. metricTags]; }

        // Non-vacuous on both channels — and specifically the replacement span this
        // integration emits instead of the suppressed built-in one, with its bounded
        // operation tag present.
        Assert.NotEmpty(capturedSnapshot);
        Assert.NotEmpty(metricTagsSnapshot);
        Assert.Contains(capturedSnapshot, a => a.OperationName == ApiTelemetry.GitLabFetchSpan
            && a.Tags.Any(t => t.Key == ApiTelemetry.GitLabOperationTag));
        Assert.Contains(metricTagsSnapshot, t => t.StartsWith("rocketwiki.gitlab.", StringComparison.Ordinal));

        var violations = new List<string>();
        foreach (var activity in capturedSnapshot)
        {
            Check(violations, $"span '{activity.DisplayName}' (source '{activity.Source.Name}') name", activity.DisplayName);
            Check(violations, $"span '{activity.DisplayName}' operation name", activity.OperationName);
            foreach (var (key, value) in activity.Tags)
            {
                Check(violations, $"tag key on span '{activity.DisplayName}' (source '{activity.Source.Name}')", key);
                Check(violations, $"tag '{key}' on span '{activity.DisplayName}' (source '{activity.Source.Name}')", value);
            }

            foreach (var activityEvent in activity.Events)
            {
                Check(violations, $"event on span '{activity.DisplayName}'", activityEvent.Name);
                foreach (var (key, value) in activityEvent.Tags)
                {
                    Check(violations, $"event tag '{key}' on span '{activity.DisplayName}'", value?.ToString());
                }
            }

            foreach (var (key, value) in activity.Baggage)
            {
                Check(violations, $"baggage '{key}' on span '{activity.DisplayName}'", value);
            }
        }

        foreach (var tag in metricTagsSnapshot)
        {
            Check(violations, "metric instrument name or tag", tag);
        }

        Assert.True(violations.Count == 0,
            "design.md §15 (GitLab amendment) forbids project paths, file paths, issue titles, and " +
            $"filter text in telemetry. Found {violations.Count} violation(s):\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The production defense for the built-in HttpClient span: the gitlab named
    /// client's real handler chain (the BASE factory — production wiring, no fake)
    /// must bottom out in a SocketsHttpHandler with ActivityHeadersPropagator = null,
    /// which is the documented way to remove the DiagnosticsHandler — no
    /// url.full-carrying span, and no trace context handed to GitLab (§15 propagates
    /// only to the API's own origin).
    /// </summary>
    [Fact]
    public void TheRealGitLabClient_HasNoActivityPropagator()
    {
        var handlerFactory = fixture.BaseFactory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = handlerFactory.CreateHandler(GitLabConfiguration.HttpClientName);

        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            handler = delegating.InnerHandler;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Null(sockets.ActivityHeadersPropagator);
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
                violations.Add($"{location} contains {sentinel}: '{(value.Length <= 200 ? value : value[..200] + "...")}'");
            }
        }
    }
}
