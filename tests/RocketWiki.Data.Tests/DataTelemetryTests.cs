using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Telemetry;
using RocketWiki.Data.Services;
using RocketWiki.Data.Telemetry;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// design.md §15/§16: proves the persistence layer's instruments fire, and — the part
/// that matters more than the counts — that they fire <b>only on commit</b>. A counter
/// incremented while building the change set would report writes that a rolled-back
/// transaction never made, which is exactly the kind of quietly-wrong operational data
/// that erodes trust in a dashboard.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public class DataTelemetryTests : SqliteTestBase
{
    private const string LocalInstanceId = "local-instance";
    private static readonly AuditContext AuditCtx = new(AuditChannel.GraphQl, "req-telemetry", "127.0.0.1");

    private static Principal EditorPrincipal() => Principal.Create("user-sub", []);

    private static Space NewExportedSpace(string key = "TEL") => new()
    {
        Key = key,
        Name = $"{key} Space",
        OriginInstanceId = LocalInstanceId,
        IsExported = true,
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
    };

    private static AccessRule EditorGrant(Guid spaceId) => new()
    {
        Kind = AccessRuleKind.SpaceGrant,
        SpaceId = spaceId,
        Role = SpaceRole.Editor,
        ExpressionJson = """{ "everyone": true }""",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
        UpdatedAtUtc = DateTime.UtcNow,
        UpdatedByUserId = Guid.NewGuid(),
    };

    private static (ActivityListener Listener, List<Activity> Captured) ListenToData()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DataTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, captured);
    }

    [Fact]
    public async Task SubtreeDelete_EmitsOneSpanCarryingTheRootPageIdAndTheNumberOfPagesDeleted()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var root = TestData.NewPage(space, "root");

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(root);
        context.SaveChanges();

        var child = TestData.NewPage(space, "child", root);
        context.Pages.Add(child);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);

        var (listener, captured) = ListenToData();
        using (listener)
        {
            var result = await service.DeletePageAsync(
                new DeletePageRequest(root.Id), EditorPrincipal(), actor.Id, AuditCtx);
            Assert.True(result.IsSuccess);
        }

        var span = Assert.Single(captured);
        Assert.Equal(DataTelemetry.SubtreeDeleteSpan, span.OperationName);
        Assert.Equal(root.Id, span.GetTagItem(DataTelemetry.PageIdTag));
        Assert.Equal(2, span.GetTagItem(DataTelemetry.PageCountTag));
        Assert.Equal(DataTelemetry.SuccessOutcome, span.GetTagItem(DataTelemetry.OutcomeTag));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [Fact]
    public async Task AFailedMutation_TagsTheSpanWithTheErrorTypeNameAndNeverTheErrorContents()
    {
        // StaleRevisionError carries LatestTitle and LatestContent. design.md §15 makes
        // putting either on a span a violation, so only the type name is recorded.
        var actor = TestData.NewUser();
        var space = NewExportedSpace();
        var page = TestData.NewPage(space);
        page.CurrentRevisionNumber = 2;
        page.Title = "SENTINELTITLE";
        page.CurrentContent = "SENTINELCONTENT";

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.Pages.Add(page);
        context.PageRevisions.Add(TestData.NewRevision(page, actor, 1));
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var service = new PageService(context, LocalInstanceId);

        var (listener, captured) = ListenToData();
        using (listener)
        {
            // Restoring revision 1 while claiming the current revision is 1, when it is
            // actually 2 - a stale-revision conflict.
            var result = await service.RestoreRevisionAsync(
                new RestoreRevisionRequest(page.Id, 1, 1), EditorPrincipal(), actor.Id, AuditCtx);
            Assert.False(result.IsSuccess);
            Assert.IsType<StaleRevisionError>(result.Error);
        }

        var span = Assert.Single(captured);
        Assert.Equal(nameof(StaleRevisionError), span.GetTagItem(DataTelemetry.OutcomeTag));
        Assert.Equal(ActivityStatusCode.Error, span.Status);

        foreach (var (key, value) in span.Tags)
        {
            Assert.DoesNotContain("SENTINEL", $"{key}={value}", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task OutboxAppend_IncrementsTheCounterTaggedWithTheSyncEventType()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        using var outbox = new MetricCollector<long>(DataTelemetry.Meter, "rocketwiki.sync.outbox_entries_appended");
        using var domainEvents = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.domain_events.raised");

        var service = new PageService(context, LocalInstanceId);
        var result = await service.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(result.IsSuccess);

        Assert.Contains(outbox.GetMeasurementSnapshot(),
            m => Equals(m.Tags[DataTelemetry.SyncEventTypeTag], nameof(SyncEventType.PageUpsert)));
        Assert.Contains(domainEvents.GetMeasurementSnapshot(),
            m => Equals(m.Tags[CoreTelemetry.DomainEventTypeTag], nameof(PageCreatedEvent)));
    }

    [Fact]
    public void NothingIsCountedWhenTheTransactionNeverCommits()
    {
        // The pipeline builds the audit row and the outbox row in memory, then saves. If
        // the save throws, none of it landed - and none of it must be counted. Forced
        // here with a foreign key violation on the actor, which SQLite does enforce
        // (see SqliteTestBase) and which surfaces from SaveChanges, after
        // ProcessPendingDomainEvents has already run.
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Spaces.Add(space);
        context.SaveChanges();

        using var outbox = new MetricCollector<long>(DataTelemetry.Meter, "rocketwiki.sync.outbox_entries_appended");
        using var domainEvents = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.domain_events.raised");
        using var audit = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.audit.events_written");

        var page = TestData.NewPage(space);
        context.Pages.Add(page);
        context.AuditContext = AuditCtx;
        context.RaiseDomainEvent(new PageCreatedEvent(page.Id, space.Id, space.Key, Guid.NewGuid(), page.Title));

        Assert.ThrowsAny<Exception>(() => context.SaveChanges());

        Assert.Empty(outbox.GetMeasurementSnapshot());
        Assert.Empty(domainEvents.GetMeasurementSnapshot());
        Assert.Empty(audit.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task BundleExport_EmitsASpanWithTheBundleNumberAndEntryCount()
    {
        var actor = TestData.NewUser();
        var space = NewExportedSpace();

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.Add(space);
        context.AccessRules.Add(EditorGrant(space.Id));
        context.SaveChanges();

        var pageService = new PageService(context, LocalInstanceId);
        Assert.True((await pageService.CreatePageAsync(
            new CreatePageRequest(space.Id, null, "home", "Home", "# Home"), EditorPrincipal(), actor.Id, AuditCtx)).IsSuccess);

        var outputDirectory = Path.Combine(Path.GetTempPath(), "rocketwiki-bundle-telemetry-" + Guid.NewGuid());
        try
        {
            var exportService = new BundleExportService(context, new NullFileStorage());

            var (listener, captured) = ListenToData();
            using (listener)
            {
                var info = await exportService.ExportIncrementalAsync(outputDirectory, LocalInstanceId);
                Assert.NotNull(info);
            }

            var span = Assert.Single(captured);
            Assert.Equal(DataTelemetry.BundleExportIncrementalSpan, span.OperationName);
            Assert.Equal(1, span.GetTagItem(DataTelemetry.BundleNumberTag));
            Assert.Equal(1, span.GetTagItem(DataTelemetry.BundleEntryCountTag));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    /// <summary>No attachments in these fixtures, so blob storage is never reached.</summary>
    private sealed class NullFileStorage : Storage.IFileStorage
    {
        public Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(false);
    }
}
