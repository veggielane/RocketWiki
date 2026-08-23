using Microsoft.EntityFrameworkCore;
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
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<PageLabel> PageLabels => Set<PageLabel>();
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
    public DbSet<CustomEmoji> CustomEmojis => Set<CustomEmoji>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RocketWikiDbContext).Assembly);

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
        }

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var telemetry = ProcessPendingDomainEvents();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        telemetry.RecordCommitted();
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var telemetry = ProcessPendingDomainEvents();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        telemetry.RecordCommitted();
        return result;
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
