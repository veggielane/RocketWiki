using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// design.md §15: every RocketWiki project declares its <c>ActivitySource</c> and
    /// <c>Meter</c> under its own assembly name (<c>RocketWiki.Core</c>,
    /// <c>RocketWiki.Data</c>, ...), so one wildcard subscribes to all of them and to
    /// any project added later. It has to be a wildcard rather than an explicit list
    /// because this project cannot reference the projects whose names it would list —
    /// every service references ServiceDefaults, so a reference back would be circular.
    /// A guard test (<c>TelemetrySourceNamingTests</c>) asserts every telemetry class
    /// actually uses a name this pattern matches, so the indirection can't silently rot.
    /// </summary>
    public const string RocketWikiMeterAndSourceWildcard = "RocketWiki.*";

    /// <summary>
    /// Instrumentation that ships inside .NET or a library and only needs subscribing
    /// to, verified against the current docs rather than assumed:
    /// <list type="bullet">
    /// <item><c>Microsoft.AspNetCore.SignalR.Server</c> — one activity per hub method
    /// invocation, built into ASP.NET Core since .NET 9. Hub activities are deliberately
    /// parentless (they aren't nested under the long-lived connection), so without this
    /// source a <c>JoinPage</c> call is invisible.</item>
    /// <item><c>HotChocolate.Diagnostics</c> — the GraphQL execution pipeline. The
    /// package is referenced by RocketWiki.Api, which configures what may appear on
    /// those spans (see Program.cs); this end only subscribes. Named by string rather
    /// than via the package's own <c>AddHotChocolateInstrumentation()</c> helper so
    /// ServiceDefaults keeps no GraphQL dependency of its own.</item>
    /// </list>
    /// SQL is absent on purpose: Aspire's <c>AddSqlServerDbContext</c> already calls
    /// <c>AddSqlClientInstrumentation()</c> itself (confirmed by decompiling
    /// Aspire.Microsoft.EntityFrameworkCore.SqlServer 13.5.1), and registering it twice
    /// would double every database span.
    /// </summary>
    private static readonly string[] ThirdPartySources =
    [
        "Microsoft.AspNetCore.SignalR.Server",
        "HotChocolate.Diagnostics",
    ];

    /// <summary>
    /// Built-in meters that are not part of <c>AddAspNetCoreInstrumentation</c>:
    /// <list type="bullet">
    /// <item><c>Microsoft.AspNetCore.Http.Connections</c> — SignalR connection metrics
    /// (<c>signalr.server.active_connections</c>, connection duration). This is the
    /// only view of whether presence connections are accumulating or churning.</item>
    /// <item><c>Microsoft.EntityFrameworkCore</c> — EF Core's own metrics
    /// (<c>microsoft.entityframeworkcore.active_dbcontexts</c>, queries, savechanges,
    /// compiled-query cache hits/misses), available since EF Core 9. Aspire's client
    /// integration registers SQL Client <i>tracing</i> only and no metrics at all, so
    /// this is a genuine gap it leaves rather than a duplicate. Active-DbContext count
    /// is the leak signal for a pooled context, which this app uses.</item>
    /// <item><c>Microsoft.AspNetCore.Authorization</c> / <c>.Authentication</c> —
    /// .NET 10 security metrics. Directly relevant to a fail-closed system: an
    /// authorization failure rate that moves is a thing to notice. Their attributes are
    /// bounded (<c>user.is_authenticated</c> as a boolean, policy name, success/failure,
    /// scheme name) and carry no claim values, so they satisfy §15 as shipped.</item>
    /// </list>
    /// </summary>
    private static readonly string[] ThirdPartyMeters =
    [
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Authorization",
        "Microsoft.AspNetCore.Authentication",
    ];

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Every RocketWiki project's own Meter (design.md §15). Named
                    // after the assembly, so one wildcard covers Core, Data, Api,
                    // Storage and Importer, and any project added later.
                    .AddMeter(RocketWikiMeterAndSourceWildcard)
                    .AddMeter(ThirdPartyMeters);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    // Every RocketWiki project's own ActivitySource; same wildcard
                    // convention as the meters above.
                    .AddSource(RocketWikiMeterAndSourceWildcard)
                    .AddSource(ThirdPartySources)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/aspire/healthchecks for details before enabling these endpoints in non-development environments:
        // they are unauthenticated (a kubelet probe cannot present a token), and /health
        // aggregates dependency state — reachable from the open network, that is free
        // reconnaissance ("their database is down") and a cheap availability probe.
        //
        // So outside Development they stay OFF unless explicitly opted into via
        // HealthEndpoints:Enabled (default false — fail closed, same posture as every
        // other guard in this codebase). The opt-in exists for orchestrated
        // deployments where the endpoints never reach the open network anyway: on k3s,
        // liveness/readiness probes come from the kubelet over the pod network, and
        // keeping /health and /alive unrouted at the ingress is the Helm chart's job
        // (deploy/helm/rocketwiki — the chart both sets HealthEndpoints__Enabled for
        // its HTTP probe mode and deliberately routes neither path at Traefik). This
        // method's job is only to refuse to expose them by accident.
        var enabled = app.Environment.IsDevelopment()
            || app.Configuration.GetValue("HealthEndpoints:Enabled", defaultValue: false);
        if (enabled)
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
