using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §15/§19: the anonymous Gravatar route's path carries an email hash, so
/// ServiceDefaults keeps it out of telemetry. Tracing was excluded from the start; this
/// covers the half that was open until now — <b>exported logs</b>. With
/// <c>IncludeScopes = true</c> (ServiceDefaults, deliberately: it is what correlates a
/// log line to a request), ASP.NET Core hosting's <c>RequestPath</c> scope rides on every
/// record emitted during a request, and hosting's own "Request starting/finished" lines
/// carry the path in their message text as well. Either one exports the hash.
///
/// The guarantee is structural — records emitted under <c>/avatar/{hash}</c> never reach
/// the OpenTelemetry logger provider at all — so this test asserts it where it can
/// actually be observed: at an in-memory exporter attached to the same pipeline the OTLP
/// exporter would occupy. The control request in the same test is what makes it
/// non-vacuous: it proves this host really does export log records, with their scopes,
/// while the gravatar request produced nothing.
/// </summary>
public sealed class GravatarLogExclusionTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private readonly List<string> _exported = [];

    /// <summary>
    /// The shared fixture's host plus two things: request-level logging turned on
    /// (appsettings pins Microsoft.AspNetCore to Warning, which would leave nothing to
    /// capture) and an in-memory log exporter on the OpenTelemetry logging pipeline.
    /// </summary>
    private WebApplicationFactory<Program> CapturingHost() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Information",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
                    ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information",
                }));

            builder.ConfigureLogging(logging => logging.AddOpenTelemetry(options =>
                options.AddProcessor(new SimpleLogRecordExportProcessor(new CapturingLogExporter(_exported)))));
        });

    [Fact]
    public async Task NoLogRecordFromTheGravatarRouteIsExported_WhileOrdinaryRequestsStillAre()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // MD5-shaped, matches no user
        using var host = CapturingHost();
        var anonymous = host.CreateClient();

        await anonymous.GetAsync($"/avatar/{hash}");

        // Control: a route with nothing sensitive in its path, through the same host and
        // the same pipeline, immediately afterwards.
        await anonymous.GetAsync("/health");

        string[] captured;
        lock (_exported)
        {
            captured = [.. _exported];
        }

        // Non-vacuous on both axes: records are exported at all, and their scopes really
        // do carry RequestPath (if they didn't, "no hash in the export" would prove
        // nothing about the leak this closes).
        Assert.NotEmpty(captured);
        Assert.Contains(captured, record => record.Contains("RequestPath=/health", StringComparison.Ordinal));

        Assert.DoesNotContain(captured, record => record.Contains(hash, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(captured, record => record.Contains("/avatar/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Flattens each record to "message + every scope key=value" — the two places
    /// the request path actually reaches an exporter.</summary>
    private sealed class CapturingLogExporter(List<string> sink) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var flattened = new StringBuilder(record.FormattedMessage ?? record.Body ?? string.Empty);
                record.ForEachScope((scope, state) =>
                {
                    foreach (var item in scope)
                    {
                        state.Append(' ').Append(item.Key).Append('=').Append(item.Value);
                    }
                }, flattened);

                lock (sink)
                {
                    sink.Add(flattened.ToString());
                }
            }

            return ExportResult.Success;
        }
    }
}
