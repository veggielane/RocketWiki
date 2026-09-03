using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;

namespace RocketWiki.Data;

/// <summary>
/// Threads the instance's configured <see cref="SelectorCatalog"/> (design.md §21.15)
/// into <see cref="RocketWikiDbContext"/>, exactly as
/// <see cref="LocalInstanceDbContextOptionsExtension"/> threads the instance id and for
/// the same reason: the catalog is per-deployment configuration, the context is
/// registered <b>pooled</b>, and thirteen services construct their own
/// <c>PermissionContextLoader</c> over the context — so the one place every gate can
/// reliably read the catalog from is the context's options, set once where they are
/// built (Program.cs, a test base, a CLI) and immutable for the context's lifetime.
///
/// <para><b>Absent means <see cref="SelectorCatalog.Empty"/></b>, which is the
/// fail-closed catalog: an instance that never declared one knows no selector, so a page
/// carrying one is readable by nobody. There is no "absent means skip the gate" reading,
/// for the same reason the calculator has no nullable-marking overload.</para>
/// </summary>
public sealed class SelectorCatalogDbContextOptionsExtension(SelectorCatalog catalog) : IDbContextOptionsExtension
{
    public SelectorCatalog Catalog { get; } = catalog;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // Carries a value only; contributes no EF services.
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(SelectorCatalogDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new SelectorCatalogDbContextOptionsExtension Extension =>
            (SelectorCatalogDbContextOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        // The COUNT only, never a category name: a selector category is marking
        // vocabulary, and EF's context-initialized log line is telemetry (design.md
        // §15) — the same discipline that keeps a category name out of a metric tag.
        public override string LogFragment => $"SelectorCategories={Extension.Catalog.Count} ";

        // The value doesn't affect which internal EF service provider is required.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["RocketWiki:SelectorCategories"] = Extension.Catalog.Count.ToString();
    }
}

public static class SelectorCatalogDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Declares the instance's selector catalog (design.md §21.15) for every gate that
    /// runs over this context. Build it once at startup with
    /// <see cref="SelectorCatalog.Create"/> and pass the same instance the API registers
    /// as a singleton, so the formatter and the gate read one vocabulary.
    /// </summary>
    public static DbContextOptionsBuilder UseSelectorCatalog(
        this DbContextOptionsBuilder optionsBuilder, SelectorCatalog catalog)
    {
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new SelectorCatalogDbContextOptionsExtension(catalog));
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseSelectorCatalog(DbContextOptionsBuilder, SelectorCatalog)"/>
    public static DbContextOptionsBuilder<TContext> UseSelectorCatalog<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, SelectorCatalog catalog)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseSelectorCatalog((DbContextOptionsBuilder)optionsBuilder, catalog);
}
