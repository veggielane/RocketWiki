using Microsoft.Extensions.Options;
using RocketWiki.Core.Access;
using RocketWiki.Data;

namespace RocketWiki.Api.Markings;

/// <summary>
/// Wires the protective-marking configuration (design.md §21.15): the
/// <c>ProtectiveMarking:SelectorCategories</c> section becomes ONE validated
/// <see cref="SelectorCatalog"/>, registered as a singleton for the GraphQL vocabulary
/// query and every formatter, and stamped into the DbContext
/// options (<c>UseSelectorCatalog</c>) for every gate the data layer runs. One object,
/// both consumers — the formatter and the gate cannot read two vocabularies.
///
/// <para><b>Invalid configuration fails the host at startup</b> (design.md §15's
/// fail-closed configuration family, the same posture as the upload caps): the options
/// registration carries <c>ValidateOnStart</c>, whose validator is the catalog's own
/// <see cref="SelectorCatalog.TryCreate"/>, so a mis-typed category refuses the process
/// before a single request is served. A wiki that silently dropped a mis-typed category
/// would be enforcing a vocabulary its operator did not configure.</para>
///
/// <para><b>Built lazily, from the bound options, not eagerly off the builder.</b> The
/// first version read the section at the top of <c>Program.cs</c>, because the DbContext
/// options used to be configured through a callback with no service provider. That
/// read happened before a test host's configuration is applied — a
/// <c>WebApplicationFactory</c> layers its in-memory settings in when the host is built,
/// after the top-level statements have run — so the API test tier ran every gate against
/// <see cref="SelectorCatalog.Empty"/> while its own configuration said otherwise, and
/// nothing noticed until the first test needed a category. EF Core's
/// <c>ConfigureDbContext</c> hook takes a service provider, so the catalog can be resolved
/// where it is stamped and the singleton can be built from <see cref="IOptions{TOptions}"/>
/// like every other options family. Production reads the same values either way; only
/// the moment of the read moved.</para>
///
/// <para>Telemetry (§15): nothing here logs a category name or value. The startup line
/// EF emits carries the category count only (see
/// <c>SelectorCatalogDbContextOptionsExtension</c>).</para>
/// </summary>
public static class ProtectiveMarkingConfiguration
{
    public static WebApplicationBuilder AddRocketWikiProtectiveMarking(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<ProtectiveMarkingOptions>()
            .Bind(builder.Configuration.GetSection(ProtectiveMarkingOptions.SectionName))
            .Validate(
                options => SelectorCatalog.TryCreate(ToDefinitions(options), out _, out _),
                "ProtectiveMarking:SelectorCategories is invalid; see the startup exception for the failing category.")
            .ValidateOnStart();

        // One instance for the process: the GraphQL vocabulary,
        // every formatter and (through ConfigureDbContext below) every gate read this
        // same object. SelectorCatalog.Create throws on the first invalid definition,
        // naming the category — the same rule ValidateOnStart already enforced.
        builder.Services.AddSingleton(sp =>
            SelectorCatalog.Create(ToDefinitions(sp.GetRequiredService<IOptions<ProtectiveMarkingOptions>>().Value)));

        // The data layer's half (design.md §21.15): stamped into the context options,
        // where the pooled context and every DataLoader-owned context read it — the same
        // pattern as the local instance id, resolved here rather than captured eagerly.
        builder.Services.ConfigureDbContext<RocketWikiDbContext>((sp, options) =>
            options.UseSelectorCatalog(sp.GetRequiredService<SelectorCatalog>()));

        return builder;
    }

    /// <summary>The options shape to Core's definition record; canonicalization and
    /// validation happen in <see cref="SelectorCatalog.TryCreate"/>, not here.</summary>
    public static IEnumerable<SelectorCategory> ToDefinitions(ProtectiveMarkingOptions options) =>
        (options.SelectorCategories ?? []).Select(c =>
            new SelectorCategory(c.Name ?? string.Empty, c.Description, c.Values ?? []));
}
