using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace RocketWiki.Data;

/// <summary>
/// Threads the local instance id (design.md §12: every instance has an
/// <c>InstanceId</c>) into <see cref="RocketWikiDbContext"/> so the sync outbox writer
/// can enforce, not assume, that only spaces this instance owns
/// (<c>Space.OriginInstanceId == local</c>) ever journal sync events — §12's stated
/// follow-up on the outbox writer trusting <c>Space.IsExported</c> alone.
///
/// Why an EF options extension rather than a settable property like
/// <see cref="RocketWikiDbContext.AuditContext"/>: the API registers the context
/// <b>pooled</b> (Aspire's <c>AddSqlServerDbContext</c>), so per-instance constructor
/// injection is unavailable and a mutable property would have to be re-set by every
/// unit of work — one forgotten call site and the invariant silently rots. The instance
/// id is per-deployment configuration, exactly what <see cref="DbContextOptions"/>
/// carries: set once where the options are built (Program.cs, a test base, a CLI), it
/// is immutable for the context's lifetime and pool-safe by construction. AuditContext
/// stays a property because it genuinely varies per request; this genuinely doesn't.
/// </summary>
public sealed class LocalInstanceDbContextOptionsExtension(string localInstanceId) : IDbContextOptionsExtension
{
    public string LocalInstanceId { get; } = localInstanceId;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // Carries a value only; contributes no EF services.
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(LocalInstanceDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new LocalInstanceDbContextOptionsExtension Extension =>
            (LocalInstanceDbContextOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        // The instance id is deployment topology ("standalone", "low", "high"), not
        // content or identity, so surfacing it in EF's context-initialized log line
        // is within design.md §15's telemetry rules.
        public override string LogFragment => $"LocalInstanceId={Extension.LocalInstanceId} ";

        // The value doesn't affect which internal EF service provider is required.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["RocketWiki:LocalInstanceId"] = Extension.LocalInstanceId;
    }
}

public static class LocalInstanceDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Declares which instance (design.md §12) the configured database belongs to.
    /// Required wherever mutations on exported spaces can occur: the sync outbox
    /// writer refuses (loudly) to journal for an exported space when this was never
    /// configured, and silently skips spaces whose <c>OriginInstanceId</c> doesn't
    /// match — see <see cref="SyncOutboxWriter"/> for the reasoning behind each.
    /// </summary>
    public static DbContextOptionsBuilder UseLocalInstanceId(
        this DbContextOptionsBuilder optionsBuilder, string localInstanceId)
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new LocalInstanceDbContextOptionsExtension(localInstanceId));
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseLocalInstanceId(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<TContext> UseLocalInstanceId<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, string localInstanceId)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseLocalInstanceId((DbContextOptionsBuilder)optionsBuilder, localInstanceId);
}
