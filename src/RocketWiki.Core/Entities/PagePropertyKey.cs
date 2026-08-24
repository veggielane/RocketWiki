namespace RocketWiki.Core.Entities;

/// <summary>
/// One allowed key in the instance's admin-defined page-property registry
/// (design.md §20). Page editors pick a key from this list and supply a value; they
/// never invent keys. The registry exists precisely so "Owner" and "owner" cannot both
/// become properties of the same wiki — free-form keys would let a report over
/// properties (§20's stated future) silently split on capitalization.
///
/// Instance-local, like the custom-emoji registry: the table is never exported and a
/// sync bundle carries the key's <b>name</b>, not this row's id, so a replica
/// materializes whatever key it needs on import (see BundleImportService.ApplyPagePropertyAsync).
/// </summary>
public class PagePropertyKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The display form, exactly as the admin typed it (trimmed) — "Owner",
    /// "Review Date". This is what the properties screen shows and what a sync
    /// payload carries.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Key"/> lowercased with the invariant culture — the column the unique
    /// index actually lives on. See <see cref="Normalize"/> for why uniqueness cannot
    /// be left to the database's collation.
    /// </summary>
    public string KeyNormalized { get; set; } = string.Empty;

    /// <summary>Optional admin hint rendered next to the key on the properties screen.</summary>
    public string? Description { get; set; }

    /// <summary>Display order on the properties screen and in <c>Page.properties</c>; ties break on <see cref="Key"/>.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Null when the row was materialized by a sync import rather than created by a
    /// local admin (design.md §20): the bundle payload carries the key's name only, so
    /// there is no local actor to attribute — the same "system action, no user" shape
    /// <c>AuditEvent.UserId</c> already has.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    public User? CreatedBy { get; set; }

    public ICollection<PageProperty> PageProperties { get; set; } = new List<PageProperty>();

    /// <summary>
    /// THE normalization for key uniqueness and key lookup — every caller (the service,
    /// the sync importer) goes through here so no two of them can disagree.
    ///
    /// <para>Why the application normalizes instead of the database: SQL Server's default
    /// collation is case-INsensitive, SQLite's is case-sensitive for ASCII. A unique index
    /// on the raw <see cref="Key"/> would therefore mean "Owner" and "owner" collide in
    /// production and coexist in the SQLite test tier — the tiers would be testing
    /// different rules, and the tier that matters least would be the one that passes.
    /// Normalizing here and indexing <see cref="KeyNormalized"/> makes the rule identical
    /// on both providers. Same instinct as SqlServerFileStorage's BIN2 key column: never
    /// let a collation default decide a correctness question.</para>
    /// </summary>
    public static string Normalize(string key) => key.Trim().ToLowerInvariant();
}
