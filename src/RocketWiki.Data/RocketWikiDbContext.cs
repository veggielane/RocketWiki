using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Events;

namespace RocketWiki.Data;

public class RocketWikiDbContext : DbContext
{
    private readonly List<IDomainEvent> _pendingDomainEvents = new();

    public RocketWikiDbContext(DbContextOptions<RocketWikiDbContext> options)
        : base(options)
    {
    }

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
        ProcessPendingDomainEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ProcessPendingDomainEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
    private void ProcessPendingDomainEvents()
    {
        if (_pendingDomainEvents.Count == 0)
        {
            return;
        }

        if (AuditContext is null)
        {
            throw new MissingAuditContextException();
        }

        var timestampUtc = DateTime.UtcNow;
        foreach (var domainEvent in _pendingDomainEvents)
        {
            AuditEvents.Add(DomainEventAuditMapper.ToAuditEvent(domainEvent, AuditContext, timestampUtc));
        }

        SyncOutboxWriter.AppendPendingEvents(this, _pendingDomainEvents);

        _pendingDomainEvents.Clear();
    }
}
