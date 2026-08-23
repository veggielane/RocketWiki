using System.Diagnostics;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Guards the wiring between "an instrument exists" and "the OpenTelemetry pipeline is
/// actually subscribed to it" (design.md §15). Two failure modes this catches:
///
/// 1. A telemetry class named outside the <c>RocketWiki.*</c> convention. ServiceDefaults
///    subscribes by wildcard because it cannot reference the projects it would otherwise
///    have to list by name (everything references ServiceDefaults, not the reverse), so
///    the convention is load-bearing: a source called <c>Wiki.Storage</c> would compile,
///    emit, and be silently dropped.
/// 2. A third-party source name string that no longer matches what the library emits.
///    <c>Microsoft.AspNetCore.SignalR.Server</c> and <c>HotChocolate.Diagnostics</c> are
///    hard-coded strings in ServiceDefaults; these tests drive real traffic and assert
///    those exact names appear.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryRegistrationTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    /// <summary>
    /// The names ServiceDefaults' <c>RocketWiki.*</c> wildcard has to cover. Referenced
    /// as live symbols rather than string literals, so a rename that breaks the
    /// convention fails here rather than at runtime in a dashboard nobody is watching.
    /// </summary>
    public static TheoryData<string, string> RocketWikiTelemetryNames() => new()
    {
        { "Core ActivitySource", CoreTelemetry.ActivitySource.Name },
        { "Core Meter", CoreTelemetry.Meter.Name },
        { "Data ActivitySource", Data.Telemetry.DataTelemetry.ActivitySource.Name },
        { "Data Meter", Data.Telemetry.DataTelemetry.Meter.Name },
        { "Api ActivitySource", RocketWiki.Api.Telemetry.ApiTelemetry.ActivitySource.Name },
        { "Api Meter", RocketWiki.Api.Telemetry.ApiTelemetry.Meter.Name },
        { "Storage ActivitySource", RocketWiki.Storage.StorageTelemetry.ActivitySource.Name },
        { "Storage Meter", RocketWiki.Storage.StorageTelemetry.Meter.Name },
        // RocketWiki.Importer is covered by the same assertion in its own test project;
        // this one isn't in the API's reference graph.
    };

    // The first parameter only distinguishes the cases: several of these names are
    // identical by design (a project's source and meter share its assembly name), and
    // xUnit dedupes theory rows whose arguments all match.
    [Theory]
    [MemberData(nameof(RocketWikiTelemetryNames))]
    public void EveryCustomSourceAndMeterIsCoveredByTheServiceDefaultsWildcard(string which, string name)
    {
        Assert.NotEmpty(which);
        // "RocketWiki.*" - the literal is repeated here rather than referenced so that
        // changing the wildcard in ServiceDefaults is a deliberate two-place edit.
        Assert.StartsWith("RocketWiki.", name, StringComparison.Ordinal);
        Assert.True(name.Length > "RocketWiki.".Length, $"'{name}' has nothing after the prefix.");
    }

    [Fact]
    public async Task AGraphQLRequestEmitsOnTheHotChocolateSourceServiceDefaultsSubscribesTo()
    {
        var captured = new List<Activity>();
        using var listener = Listen(captured, "HotChocolate.Diagnostics");

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"hc-{Guid.NewGuid()}");
        using var _ = await client.PostGraphQLAsync("query { me { id isAuthenticated } }");

        Assert.NotEmpty(captured);
    }

    [Fact]
    public async Task AHubInvocationEmitsOnTheBuiltInSignalRServerSource()
    {
        // Confirms the exact ActivitySource name .NET's SignalR server uses. Hub method
        // activities are parentless, so without this subscription a JoinPage call would
        // leave no trace at all - which is precisely what makes the string worth pinning.
        var captured = new List<Activity>();
        using var listener = Listen(captured, "Microsoft.AspNetCore.SignalR.Server");

        var claimsHeader = TestUserHttpClientExtensions.BuildEncodedClaimsHeaderValue($"sr-{Guid.NewGuid()}");
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add(TestAuthHandler.ClaimsHeaderName, claimsHeader);
            })
            .Build();

        await using (connection)
        {
            await connection.StartAsync();
            await connection.InvokeAsync("LeavePage", Guid.NewGuid());
        }

        // A connection also produces OnConnectedAsync/OnDisconnectedAsync activities on
        // this source; the hub *method* is the one worth pinning.
        Assert.Contains(captured, a => a.DisplayName.EndsWith("/LeavePage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAuditedReadIncrementsTheAuditCounterWithActionOutcomeAndChannel()
    {
        // design.md §7: resolving Page.content emits a page.view audit row via IAuditSink.
        // The counter must agree with the table, and must be tagged with the *sink*
        // writer rather than the domain-event writer, since a read is not a domain event.
        var pageId = await SeedViewablePageAsync();

        using var collector = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.audit.events_written");

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"audit-{Guid.NewGuid()}");
        using var _ = await client.PostGraphQLAsync($$"""
            query { page(id: "{{pageId}}") { id content } }
            """);

        // Contains rather than Single even though this collection is non-parallel: the
        // factory is shared per class, so a request can legitimately produce more than
        // one audit measurement, and the tags carry no page id (§15 - deliberately) to
        // filter by. What matters is that a measurement with exactly this tag set exists.
        Assert.Contains(collector.GetMeasurementSnapshot(), m =>
            Equals(m.Tags[CoreTelemetry.AuditActionTag], "page.view")
            && Equals(m.Tags[CoreTelemetry.AuditOutcomeTag], nameof(AuditOutcome.Success))
            && Equals(m.Tags[CoreTelemetry.AuditChannelTag], nameof(AuditChannel.GraphQl))
            && Equals(m.Tags[CoreTelemetry.AuditWriterTag], CoreTelemetry.AuditWriterSink)
            && m.Value == 1);
    }

    [Fact]
    public async Task AMutationIncrementsTheAuditCounterFromTheDomainEventWriter()
    {
        var spaceId = await SeedSpaceAsync();

        using var audit = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.audit.events_written");
        using var domainEvents = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.domain_events.raised");

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"mut-{Guid.NewGuid()}");
        using var response = await client.PostGraphQLAsync($$"""
            mutation {
              createPage(input: {
                spaceId: "{{spaceId}}"
                slug: "counted-{{Guid.NewGuid():N}}"
                title: "Counted"
                content: "body"
              }) { page { id } error { __typename } }
            }
            """);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, response.RootElement
            .GetProperty("data").GetProperty("createPage").GetProperty("error").ValueKind);

        // Contains rather than Single, for the same reason as above.
        Assert.Contains(audit.GetMeasurementSnapshot(), m =>
            Equals(m.Tags[CoreTelemetry.AuditActionTag], "page.create")
            && Equals(m.Tags[CoreTelemetry.AuditWriterTag], CoreTelemetry.AuditWriterDomainEvent));

        Assert.Contains(domainEvents.GetMeasurementSnapshot(),
            m => Equals(m.Tags[CoreTelemetry.DomainEventTypeTag], "PageCreatedEvent"));
    }

    private static ActivityListener Listen(List<Activity> captured, string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (captured)
                {
                    captured.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private async Task<Guid> SeedSpaceAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var seeder = new User
        {
            Subject = $"seed-{Guid.NewGuid()}",
            DisplayName = "Seeder",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
        };
        db.Users.Add(seeder);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"REG{Guid.NewGuid():N}"[..8],
            Name = "Telemetry Registration Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Editor,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = seeder.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = seeder.Id,
        });
        await db.SaveChangesAsync();

        return space.Id;
    }

    private async Task<Guid> SeedViewablePageAsync()
    {
        var spaceId = await SeedSpaceAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var page = new Page
        {
            SpaceId = spaceId,
            AncestorPath = "/",
            Slug = "registration",
            Title = "Registration Page",
            CurrentContent = "body",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }
}
