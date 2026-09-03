using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// docs/ENTRIES-AND-FORMS-PLAN.md: one structured object stored against a page.
///
/// <para>Deliberately NOT a key/value store with a nested key space. An entry has a
/// server-assigned id, a <see cref="Collection"/> naming what kind of thing it is, and a
/// JSON object. An earlier design proposed Deno-KV-style tuple keys and was dropped: an
/// order-preserving encoding is a one-way door that has to sort identically on SQL Server
/// and SQLite, and a store built for prefix range scans makes §6.7's "invisible is
/// indistinguishable from absent" far harder to hold than a flat list does.</para>
///
/// <para><b>The entry is the unit of classification.</b> It carries its own marking, so
/// one page can hold entries at several levels — which is the whole point, and also the
/// thing that breaks the old assumption that a page you can see is a page whose every
/// part you can see. Fields WITHIN an entry are never separately marked: redacting inside
/// an object returns a partial object no consumer can distinguish from a complete one. Two
/// facts needing two classifications are two entries.</para>
/// </summary>
public class PageEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    /// <summary>
    /// What kind of entry this is — a form's collection name. Stored normalized
    /// (lower-cased, invariant) because it is a lookup string, and SQL Server's default
    /// collation is case-INsensitive while SQLite's is case-sensitive for ASCII: without
    /// normalizing, "Incident" and "incident" are one collection in production and two in
    /// the test tier. The same fix <c>PagePropertyKey.KeyNormalized</c> already carries.
    ///
    /// <para>A page may hold any number of collections. A collection's identity is
    /// (page, collection) — the same name on another page is an independent set.</para>
    /// </summary>
    public string Collection { get; set; } = string.Empty;

    /// <summary>The entry itself: a JSON object at the root, never a bare scalar or array.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>
    /// Changes on every write, so a caller can compare-and-set. Not gold-plating: forms
    /// have concurrent submitters, and retro-fitting optimistic concurrency later leaves
    /// every existing caller racy in the meantime. Same shape as the page edit path's
    /// <c>ExpectedRevisionNumber</c>, deliberately, rather than a second mechanism.
    /// </summary>
    public int Version { get; set; } = 1;

    public ClassificationLevel Level { get; set; } = ClassificationLevel.Official;

    /// <summary>Presentational, exactly as <see cref="PageMarking.Prefix"/> is — never read by the gate.</summary>
    public string? Prefix { get; set; } = ProtectiveMarking.DefaultPrefix;

    /// <summary>The eyes-only country set. A child table rather than a delimited column, for
    /// the same reason <see cref="PageMarkingCountry"/> is one: a set needs set semantics.</summary>
    public ICollection<PageEntryCountry> Countries { get; set; } = new List<PageEntryCountry>();

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Null when applied by a sync import rather than written by a local user —
    /// the same "system action, no user" shape <c>PageProperty.UpdatedByUserId</c> has.</summary>
    public Guid? UpdatedByUserId { get; set; }

    public User? UpdatedByUser { get; set; }

    /// <summary>Soft delete, for the same reason page deletes are: an entry that vanished
    /// would take its sync history with it.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The comparison value the gate uses. Canonicalizes on the way out so a row
    /// hand-edited into a non-canonical state still compares correctly — same contract as
    /// <see cref="PageMarking.ToMarking"/>.
    /// </summary>
    public ProtectiveMarking ToMarking() =>
        ProtectiveMarking.Create(Level, Countries.Select(c => c.CountryValue), selectors: null, Prefix);
}

/// <summary>One country in an entry's eyes-only set. See <see cref="PageMarkingCountry"/>
/// for why this is a table rather than a delimited string.</summary>
public class PageEntryCountry
{
    public Guid PageEntryId { get; set; }

    public PageEntry? PageEntry { get; set; }

    /// <summary>Canonical (upper-case), drawn from the registered nationality attribute's
    /// allowed values — not an ISO list. See <c>PageMarkingService</c> for why.</summary>
    public string CountryValue { get; set; } = string.Empty;
}
