namespace RocketWiki.Core.Entities;

/// <summary>
/// One key/value pair on one page (design.md §20). Composite PK
/// <c>(PageId, PagePropertyKeyId)</c>: a key can carry at most one value per page, so
/// "set" is an upsert and there is no ordering question between two rows for the same
/// key. Values are plain text — no types, no validation beyond length.
///
/// <para>Properties are deliberately NOT page content: they live in their own table,
/// are edited on their own screen, and never enter the Markdown round-trip, the
/// converted-markdown corpus, or the CRDT editor.</para>
///
/// <para>Deliberately shaped so a space-level property report can be added later
/// without a data migration: the value rows already carry the page id and a registry
/// key id, which is exactly what a "every page in SPACE with key X" query needs. The
/// query surface for that does not exist yet (§20).</para>
/// </summary>
public class PageProperty
{
    public Guid PageId { get; set; }
    public Page? Page { get; set; }

    public Guid PagePropertyKeyId { get; set; }
    public PagePropertyKey? PropertyKey { get; set; }

    /// <summary>Plain text, max 1000 characters, never empty — clearing a property removes the row.</summary>
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Null when the row was applied by a sync import rather than written by a local
    /// user: §20's sync payload carries no actor, and a replica is read-only to users
    /// anyway, so every row on the high side has no local author. Same "system action"
    /// shape as <c>AuditEvent.UserId</c>.
    /// </summary>
    public Guid? UpdatedByUserId { get; set; }

    public User? UpdatedBy { get; set; }
}
