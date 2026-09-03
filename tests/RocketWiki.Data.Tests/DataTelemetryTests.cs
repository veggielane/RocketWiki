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
        Kind = AccessRuleKind.RoleGrant,
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
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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
    public async Task OutboxOwnershipMismatch_IncrementsTheMismatchCounter_AndTheHappyPathNever()
    {
        // design.md §12: a replica flagged exported is corrupt state the outbox writer
        // deliberately skips (the mutation commits, the journal stays empty). That
        // deliberate silence must be operator-VISIBLE silence:
        // rocketwiki.sync.outbox_ownership_mismatches fires exactly on the skip, and
        // never for a native exported space's ordinary journaling.
        var actor = TestData.NewUser();
        var nativeSpace = NewExportedSpace("NAT");
        var replicaSpace = NewExportedSpace("REP");
        replicaSpace.OriginInstanceId = "some-other-instance"; // replica relative to LocalInstanceId
        var replicaPage = TestData.NewPage(replicaSpace);

        using var context = CreateContext();
        context.Users.Add(actor);
        context.Spaces.AddRange(nativeSpace, replicaSpace);
        context.Pages.Add(replicaPage);
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(nativeSpace.Id)), EditorGrant(nativeSpace.Id));
        context.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.RoleGrant,
            SpaceId = replicaSpace.Id,
            Role = SpaceRole.SpaceAdmin, // rule management needs SpaceAdmin
            ExpressionJson = """{ "everyone": true }""",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        });
        context.SaveChanges();

        using var mismatches = new MetricCollector<long>(DataTelemetry.Meter, "rocketwiki.sync.outbox_ownership_mismatches");
        using var appended = new MetricCollector<long>(DataTelemetry.Meter, "rocketwiki.sync.outbox_entries_appended");

        // Happy path: a mutation on a NATIVE exported space journals normally and must
        // not touch the mismatch counter.
        var pageService = new PageService(context, LocalInstanceId);
        var created = await pageService.CreatePageAsync(
            new CreatePageRequest(nativeSpace.Id, null, "home", "Home", "# Home"), EditorPrincipal(), actor.Id, AuditCtx);
        Assert.True(created.IsSuccess);
        Assert.Single(appended.GetMeasurementSnapshot());
        Assert.Empty(mismatches.GetMeasurementSnapshot());

        // Mismatch path: rule management is the one legitimate mutation on a replica
        // (page writes die on ReadOnlyReplicaError first - see SyncOutboxTests). With
        // the corrupt IsExported flag set, the mutation commits, nothing is journaled,
        // and the mismatch counter fires once, tagged with the bounded event type.
        var ruleService = new AccessRuleService(context);
        var rule = await ruleService.CreateAsync(
            new CreateAccessRuleRequest(AccessRuleKind.PageRestriction, null, replicaPage.Id, null, PageAction.View, """{ "group": "top-secret" }"""),
            EditorPrincipal(), isInstanceAdmin: false, actor.Id, AuditCtx);
        Assert.True(rule.IsSuccess);

        var mismatch = Assert.Single(mismatches.GetMeasurementSnapshot());
        Assert.Equal(1, mismatch.Value);
        Assert.Equal(nameof(SyncEventType.Restrictions), mismatch.Tags[DataTelemetry.SyncEventTypeTag]);
        Assert.Single(appended.GetMeasurementSnapshot()); // the replica mutation journaled nothing
        Assert.Empty(context.SyncOutboxEvents.Where(e => e.SpaceId == replicaSpace.Id).ToList());
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
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));
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

    /// <summary>
    /// design.md §15's rule-engine instruments cover "effective-permission checks by
    /// canView/canEdit and denial category" — and the page tree, the busiest read path in
    /// the product, contributed nothing to them. It hand-rolls the evaluation
    /// EffectivePermissionCalculator would otherwise do (that is the whole point of
    /// walking a space in memory), and it hand-rolled its way past the calculator's
    /// telemetry with it, so classification pruning was invisible to exactly the
    /// dashboard an operator would consult about it.
    /// </summary>
    [Fact]
    public async Task PageTreeWalk_RecordsAPermissionCheckPerNode_IncludingClassificationPrunes()
    {
        using var checks = new MetricCollector<long>(CoreTelemetry.Meter, "rocketwiki.access.permission_checks");

        var space = NewExportedSpace();
        using var context = CreateContext();
        context.Spaces.Add(space);
        context.AccessRules.AddRange(TestData.AccessGrantMirroring(EditorGrant(space.Id)), EditorGrant(space.Id));

        var visible = TestData.NewPage(space, "visible");
        var secret = TestData.NewPage(space, "secret");
        context.Pages.AddRange(visible, secret);
        context.PageMarkings.Add(TestData.NewMarking(visible, ClassificationLevel.Official));
        context.PageMarkings.Add(TestData.NewMarking(secret, ClassificationLevel.Secret));
        context.SaveChanges();

        // No clearance claim, so the caller is OFFICIAL (§21.3) and the SECRET page prunes.
        var tree = await new PageReadService(context).GetPageTreeAsync(space.Id, EditorPrincipal());
        Assert.Single(Assert.IsType<ReadResult<IReadOnlyList<PageTreeEntry>>.Found>(tree).Value.OfType<PageTreeNode>());

        var measurements = checks.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Count); // one per node considered, pruned or not

        // The prune is reported under the bounded `classification` category - never the
        // level itself, which §21.8 keeps out of telemetry deliberately.
        var pruned = Assert.Single(measurements.Where(m => Equals(m.Tags[CoreTelemetry.CanViewTag], false)));
        Assert.Equal("classification", pruned.Tags[CoreTelemetry.DenialReasonTag]);

        var admitted = Assert.Single(measurements.Where(m => Equals(m.Tags[CoreTelemetry.CanViewTag], true)));
        Assert.Equal("none", admitted.Tags[CoreTelemetry.DenialReasonTag]);
    }

    [Fact]
    public void EmbeddingRun_PublishesTheQuarantinedPageCountAsAGauge()
    {
        // A quarantined page is invisible by construction: semantic search simply
        // returns fewer results, forever, and nothing fails. The gauge is the only thing
        // that says so on a dashboard, so "the gauge reports what the run recorded" is
        // the claim worth a test — a gauge whose callback was never wired reads as a
        // permanent, reassuring zero.
        using var collector = new MetricCollector<long>(DataTelemetry.Meter, "rocketwiki.embeddings.pages_quarantined");

        // A value no real run would produce, asserted by CONTAINS rather than by the
        // latest reading: other classes in this assembly drive real index runs in
        // parallel, and each one overwrites the gauge's backing field. Retried because
        // such a run can land between the record and the observation.
        const long Sentinel = 424_242;
        var observed = false;

        for (var attempt = 0; attempt < 10 && !observed; attempt++)
        {
            DataTelemetry.RecordEmbeddingRun(
                chunksEmbedded: 0, pagesSucceeded: 0, pagesFailed: 0, pagesPending: 0,
                pagesQuarantined: (int)Sentinel, elapsedSeconds: 0.1);
            collector.RecordObservableInstruments();
            observed = collector.GetMeasurementSnapshot().Any(m => m.Value == Sentinel);
        }

        Assert.True(observed,
            "rocketwiki.embeddings.pages_quarantined never reported the count the run recorded; " +
            "an operator would see a permanent zero while pages sat un-embedded.");
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
