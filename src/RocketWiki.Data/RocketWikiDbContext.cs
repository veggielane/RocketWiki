using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Telemetry;
using RocketWiki.Data.Telemetry;

namespace RocketWiki.Data;

public class RocketWikiDbContext : DbContext
{
    private readonly List<IDomainEvent> _pendingDomainEvents = new();

    public RocketWikiDbContext(DbContextOptions<RocketWikiDbContext> options)
        : base(options)
    {
        LocalInstanceId = options.FindExtension<LocalInstanceDbContextOptionsExtension>()?.LocalInstanceId;
    }

    /// <summary>
    /// This instance's id (design.md §12), from <c>UseLocalInstanceId</c> on the
    /// context options — get-only, so no unit of work can swap identity mid-flight.
    /// Null means the options never declared one; <see cref="SyncOutboxWriter"/>
    /// treats that as a configuration error the moment an exported space needs
    /// journaling, and no other consumer exists (services that need the id for
    /// authorization keep receiving it explicitly — see Program.cs).
    /// </summary>
    public string? LocalInstanceId { get; }

    /// <summary>
    /// Request/channel metadata for any audit rows this unit of work produces. The
    /// owner of this context instance (typically request-scoped middleware) must set
    /// this before any mutation raises a domain event - see <see cref="MissingAuditContextException"/>.
    /// </summary>
    public AuditContext? AuditContext { get; set; }

    /// <summary>
    /// design.md §7/§12: "all mutations flow through the domain-event pipeline". Call
    /// this alongside the entity changes that make up a mutation, then SaveChanges (or
    /// SaveChangesAsync) as usual - the corresponding AuditEvent row is appended to this
    /// same change set before the save happens, so the mutation and its audit record
    /// commit or fail together in one transaction.
    /// </summary>
    public void RaiseDomainEvent(IDomainEvent domainEvent) => _pendingDomainEvents.Add(domainEvent);

    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<PageRevision> PageRevisions => Set<PageRevision>();
    public DbSet<PageRevisionContributor> PageRevisionContributors => Set<PageRevisionContributor>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<PageLabel> PageLabels => Set<PageLabel>();
    public DbSet<PagePropertyKey> PagePropertyKeys => Set<PagePropertyKey>();
    public DbSet<PageProperty> PageProperties => Set<PageProperty>();
    public DbSet<PageMarking> PageMarkings => Set<PageMarking>();
    public DbSet<PageEntry> PageEntries => Set<PageEntry>();
    public DbSet<PageEntryCountry> PageEntryCountries => Set<PageEntryCountry>();
    public DbSet<PageMarkingCountry> PageMarkingCountries => Set<PageMarkingCountry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AccessRule> AccessRules => Set<AccessRule>();
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<KnownGroup> KnownGroups => Set<KnownGroup>();
    public DbSet<Watch> Watches => Set<Watch>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SyncOutboxEvent> SyncOutboxEvents => Set<SyncOutboxEvent>();
    public DbSet<SyncImportState> SyncImportStates => Set<SyncImportState>();
    public DbSet<SyncSpaceState> SyncSpaceStates => Set<SyncSpaceState>();
    public DbSet<PageEmbedding> PageEmbeddings => Set<PageEmbedding>();
    public DbSet<PageEmbeddingState> PageEmbeddingStates => Set<PageEmbeddingState>();
    public DbSet<GitLabCredential> GitLabCredentials => Set<GitLabCredential>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<CustomEmoji> CustomEmojis => Set<CustomEmoji>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RocketWikiDbContext).Assembly);

        // PageEntry.Collection is a lookup string, and the two providers disagree about
        // case by default: SQL Server folds it, SQLite does not. The column is normalized
        // on write, but the storage must agree too, or a normalization bug would behave
        // one way in production and another in the test tier — silently, which is how the
        // blob store's key column and the property-key registry were both caught. Applied
        // here rather than in the entity configuration because the collation NAME is
        // SQL Server's; SQLite's own default is already case-sensitive, so it needs none.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            modelBuilder.Entity<PageEntry>()
                .Property(e => e.Collection)
                .UseCollation("Latin1_General_100_BIN2");
        }

        // Compared by provider name string (rather than the Database.IsSqlite()
        // extension) so RocketWiki.Data itself never needs a package reference to the
        // SQLite provider - only test projects do.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // See SqliteAuditEventIdGenerator: SQLite cannot natively auto-generate an
            // integer column that is only part of (not the sole member of) a composite
            // primary key. SQL Server keeps native IDENTITY, as configured in
            // AuditEventConfiguration and confirmed in the generated migration.
            modelBuilder.Entity<AuditEvent>()
                .Property(e => e.Id)
                .HasValueGenerator<SqliteAuditEventIdGenerator>();

            // data-model.md: "On SQLite (tests) this table maps Embedding to a blob and
            // the in-memory cosine fallback handles search." The flat little-endian
            // float32 blob that used to be the uniform mapping is now SQLite-only —
            // SQL Server graduated to the native vector column below.
            modelBuilder.Entity<PageEmbedding>()
                .Property(e => e.Embedding)
                .HasConversion(
                    v => Configurations.PageEmbeddingConfiguration.FloatArrayToBytes(v),
                    v => Configurations.PageEmbeddingConfiguration.BytesToFloatArray(v));
        }
        else
        {
            // SQL Server (including design-time migration scaffolding, which runs on the
            // SQL Server provider via RocketWikiDbContextFactory): design.md §9.3 /
            // data-model.md — Embedding is the native SQL Server 2025 vector(1536)
            // column (AlterPageEmbeddingToNativeVector migration), written and read as
            // Microsoft.Data.SqlClient's SqlVector<float> over TDS. The entity keeps a
            // plain float[] so RocketWiki.Core stays provider-agnostic and the indexer /
            // exact-scan math is identical on every provider; the conversion below is
            // the entire provider-specific surface. The dimension count comes from
            // PageEmbeddingConfiguration.EmbeddingDimensions — fixed per column, so a
            // model/dimension change is a migration + full re-embed (data-model.md).
            modelBuilder.Entity<PageEmbedding>()
                .Property(e => e.Embedding)
                .HasColumnType($"vector({Configurations.PageEmbeddingConfiguration.EmbeddingDimensions})")
                .HasConversion(
                    v => new Microsoft.Data.SqlTypes.SqlVector<float>(v),
                    v => v.Memory.ToArray());
        }

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsurePageMarkings();
        var telemetry = ProcessPendingDomainEvents();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        telemetry.RecordCommitted();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsurePageMarkings();
        var telemetry = ProcessPendingDomainEvents();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        telemetry.RecordCommitted();
        return result;
    }

    /// <summary>
    /// design.md §21's <b>every page is marked</b> invariant, enforced at the persistence
    /// seam rather than by each write path remembering. Any <see cref="Page"/> being
    /// INSERTed in this unit of work without a <see cref="PageMarking"/> gets one before
    /// the transaction opens, so an unmarked page cannot be committed through any code
    /// path — present or future — that goes through this context.
    ///
    /// <para>This is the write-side twin of putting the clearance gate inside
    /// <c>EffectivePermissionCalculator</c>: an invariant that lives in one structural
    /// place cannot be lost one call site at a time. It is a backstop, not the feature —
    /// <c>PageService.CreatePageAsync</c> sets the marking explicitly (with real parent
    /// inheritance and an author override), and <c>BundleImportService</c> sets it
    /// explicitly (fail-closed for a bundle that carries none), so in a running instance
    /// this method has nothing left to do.</para>
    ///
    /// <para><b>It touches the database not at all</b>, deliberately: a save hook that
    /// issues queries is a save hook that can deadlock or double a round trip on every
    /// write. Parent inheritance here is therefore change-tracker-only — it covers
    /// creating a parent and a child in one unit of work — and anything else lands on
    /// <c>ProtectiveMarking.Baseline</c> (OFFICIAL, no caveat), which is the documented
    /// default for a page nobody marked. Real inheritance from a parent already in the
    /// database is <c>PageService</c>'s job, where the parent is loaded anyway.</para>
    ///
    /// <para>Note the asymmetry with the READ side, which is intentional. A missing
    /// marking on read means "something went wrong" and fails closed to TOP SECRET; a
    /// missing marking on insert means "nobody said", and the answer to that is the
    /// scheme's floor. Defaulting an insert to TOP SECRET would classify content nobody
    /// asked to classify and lock its own author out of it.</para>
    /// </summary>
    private void EnsurePageMarkings()
    {
        var insertedPages = ChangeTracker.Entries<Page>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();
        if (insertedPages.Count == 0)
        {
            return;
        }

        // Markings already in this change set (added by the caller, or by an earlier
        // iteration below) - a page never gets two.
        var markingsByPageId = ChangeTracker.Entries<PageMarking>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .ToDictionary(m => m.PageId);

        var now = DateTime.UtcNow;
        foreach (var page in insertedPages)
        {
            if (page.Marking is not null || markingsByPageId.ContainsKey(page.Id))
            {
                continue;
            }

            var inherited = page.ParentPageId is { } parentId && markingsByPageId.TryGetValue(parentId, out var parent)
                ? parent.ToMarking()
                : ProtectiveMarking.Baseline;

            var marking = new PageMarking
            {
                PageId = page.Id,
                Level = inherited.Level,
                // Inherited from the parent when there is one, ProtectiveMarking.Baseline's
                // UK otherwise (design.md §21.12).
                Prefix = inherited.Prefix,
                SetAtUtc = now,
                // No actor: nobody chose this marking, the invariant did. Same "system
                // action, no user" shape a sync-applied marking has.
                SetByUserId = null,
            };
            foreach (var country in inherited.EyesOnly)
            {
                marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = country });
            }

            PageMarkings.Add(marking);
            markingsByPageId[page.Id] = marking;
        }
    }

    /// <summary>
    /// Runs every consumer of the pending domain events - the audit writer (design.md
    /// §7), then the sync outbox writer (design.md §12) - over the SAME still-populated
    /// list, clearing it only once both have run. Both add entities to this same change
    /// set, entirely in memory, before base.SaveChanges ever opens a transaction, so a
    /// failure in either consumer (including a missing AuditContext, or the sync writer
    /// finding an untracked entity it needs) means nothing is sent to the database at
    /// all - not the audit row, not the outbox row, not the mutation raised alongside them.
    /// </summary>
    private PendingTelemetry ProcessPendingDomainEvents()
    {
        if (_pendingDomainEvents.Count == 0)
        {
            return default;
        }

        if (AuditContext is null)
        {
            throw new MissingAuditContextException();
        }

        var telemetry = new PendingTelemetry(_pendingDomainEvents.Count);

        var timestampUtc = DateTime.UtcNow;
        foreach (var domainEvent in _pendingDomainEvents)
        {
            var auditEvent = DomainEventAuditMapper.ToAuditEvent(domainEvent, AuditContext, timestampUtc);
            AuditEvents.Add(auditEvent);
            telemetry.DomainEventTypes.Add(domainEvent.GetType().Name);
            telemetry.AuditEvents.Add((auditEvent.Action, auditEvent.Outcome, auditEvent.Channel));
        }

        SyncOutboxWriter.AppendPendingEvents(
            this, _pendingDomainEvents, telemetry.OutboxEventTypes, telemetry.OutboxOwnershipMismatchEventTypes);

        _pendingDomainEvents.Clear();
        return telemetry;
    }

    /// <summary>
    /// design.md §15: counters are incremented only once <c>base.SaveChanges</c> has
    /// returned, never at the point the rows are added to the change set. Everything the
    /// pipeline produces — the mutation, its audit row, its outbox entry — commits or
    /// rolls back together, so counting at add-time would report writes that a failed
    /// transaction never made. A rolled-back save leaves these counters untouched, which
    /// is the honest reading.
    /// </summary>
    private readonly struct PendingTelemetry(int capacity)
    {
        public List<string> DomainEventTypes { get; } = new(capacity);

        public List<(string Action, AuditOutcome Outcome, AuditChannel Channel)> AuditEvents { get; } = new(capacity);

        public List<SyncEventType> OutboxEventTypes { get; } = new(capacity);

        /// <summary>Sync-relevant events the outbox writer refused to journal because the
        /// exported space belongs to another instance (corrupt replica-flagged-exported
        /// state, design.md §12). Commit-gated like everything else here: if the local
        /// mutation rolls back, no skip "happened" and nothing is counted.</summary>
        public List<SyncEventType> OutboxOwnershipMismatchEventTypes { get; } = new(capacity);

        public void RecordCommitted()
        {
            if (DomainEventTypes is null)
            {
                return; // default(PendingTelemetry) — nothing was pending
            }

            foreach (var eventType in DomainEventTypes)
            {
                CoreTelemetry.DomainEventsRaised.Add(1,
                    new KeyValuePair<string, object?>(CoreTelemetry.DomainEventTypeTag, eventType));
            }

            foreach (var (action, outcome, channel) in AuditEvents)
            {
                CoreTelemetry.RecordAuditEventWritten(action, outcome, channel, CoreTelemetry.AuditWriterDomainEvent);
            }

            foreach (var eventType in OutboxEventTypes)
            {
                DataTelemetry.RecordOutboxEntryAppended(eventType);
            }

            foreach (var eventType in OutboxOwnershipMismatchEventTypes)
            {
                DataTelemetry.RecordOutboxOwnershipMismatch(eventType);
            }
        }
    }
}
