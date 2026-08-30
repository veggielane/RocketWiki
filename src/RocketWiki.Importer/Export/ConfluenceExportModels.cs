namespace RocketWiki.Importer.Export;

/// <summary>Best-effort author identity carried through from a Confluence export. See design.md §13: "map authors by email" — this is the raw material for that, not the mapping itself (RocketWiki has no shadow-user creation service today; see the importer pipeline's notes).</summary>
public sealed record ConfluenceExportAuthor(string? Email, string? DisplayName);

/// <summary>
/// One attachment belonging to a page. <paramref name="OpenContent"/> is lazy and may be
/// called at most once productively per import (it typically wraps a
/// <see cref="System.IO.Compression.ZipArchiveEntry"/>) — the pipeline reads it exactly
/// once, to upload it.
/// </summary>
public sealed record ConfluenceExportAttachment(
    string ConfluenceAttachmentId,
    string FileName,
    string ContentType,
    Func<Stream> OpenContent);

/// <summary>
/// One comment on a page, in Confluence's own storage-format XHTML — not yet converted.
/// <paramref name="ParentConfluenceCommentId"/> is set for a threaded reply (data-model.md's
/// <c>Comment.ParentCommentId</c>); null for a top-level comment on the page itself.
/// </summary>
public sealed record ConfluenceExportComment(
    string ConfluenceCommentId,
    string? ParentConfluenceCommentId,
    string BodyXhtml,
    ConfluenceExportAuthor? Author,
    DateTimeOffset? CreatedAtUtc);

/// <summary>
/// One Confluence permission entry, carried through <b>verbatim</b> — design.md §13:
/// "Confluence permissions are reported, never translated." Nothing here is a RocketWiki
/// concept: <paramref name="Type"/> is Confluence's own permission type string
/// (<c>VIEWSPACE</c>, <c>EDITSPACE</c>, <c>View</c>, <c>Edit</c>, …) and
/// <paramref name="Subject"/> its own group name or user key. Deliberately not an enum
/// and deliberately not mapped: an automatic translation is guaranteed to be wrong in one
/// of two directions, and the over-open direction leaks export-controlled content. The
/// importer's job is to put what the source restricted in front of an admin who can
/// decide; a type this reader has never seen must therefore survive the trip intact
/// rather than be dropped as unrecognised.
/// </summary>
/// <param name="SubjectKind">"group", "user", or "anonymous" where the export says so;
/// null when it does not. A guess here would misreport who had access.</param>
public sealed record ConfluenceExportPermission(string Type, string? SubjectKind, string? Subject);

/// <summary>One page from the export, in Confluence's own storage-format XHTML — not yet converted.</summary>
public sealed record ConfluenceExportPage(
    string ConfluencePageId,
    string? ParentConfluencePageId,
    string Title,
    string StorageBodyXhtml,
    ConfluenceExportAuthor? Author,
    DateTimeOffset? CreatedAtUtc,
    IReadOnlyList<ConfluenceExportAttachment> Attachments,
    IReadOnlyList<ConfluenceExportComment>? Comments = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<ConfluenceExportPermission>? Restrictions = null)
{
    /// <summary>Threaded comments on this page, in whatever order the export listed them — see ForestOrderer for the parent-before-child ordering the importer actually needs.</summary>
    public IReadOnlyList<ConfluenceExportComment> Comments { get; init; } = Comments ?? [];

    /// <summary>Label names attached to this page. Free-form, space-scoped (data-model.md's Label) — duplicates across pages are expected and normal, not an error.</summary>
    public IReadOnlyList<string> Labels { get; init; } = Labels ?? [];

    /// <summary>
    /// Confluence page restrictions found on this page, verbatim and untranslated. The
    /// importer applies none of them; it reports them so an admin can re-apply them
    /// deliberately as RocketWiki restrictions (design.md §6.4, §13). Empty is a real
    /// answer — "the export said this page was unrestricted" — and is reported as such
    /// rather than being indistinguishable from "nobody looked".
    /// </summary>
    public IReadOnlyList<ConfluenceExportPermission> Restrictions { get; init; } = Restrictions ?? [];
}

/// <summary>A single Confluence space's worth of pages, normalized out of whatever export format produced them.</summary>
public sealed record ConfluenceExportSpace(
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<ConfluenceExportPage> Pages,
    IReadOnlyList<ConfluenceExportPermission>? Permissions = null,
    IReadOnlyList<string>? ReaderNotes = null)
{
    /// <summary>Space-level Confluence permissions, verbatim and untranslated — see <see cref="ConfluenceExportPermission"/>.</summary>
    public IReadOnlyList<ConfluenceExportPermission> Permissions { get; init; } = Permissions ?? [];

    /// <summary>
    /// What the READER dropped, in the reader's own words — blog posts, attachments
    /// whose binary is missing from the zip, comments whose owning page could not be
    /// resolved, a home page that has no equivalent here.
    ///
    /// <para>These used to be discarded silently, which made the import report's
    /// "N of M pages" denominator quietly wrong: M is this list's <c>Pages.Count</c>,
    /// already post-filter, so anything the reader excluded never appeared in either
    /// number. A migration is judged on what did NOT arrive, and RUNBOOK §5 warns that
    /// a property-name mismatch here makes "replies silently vanish" — with no counter
    /// that would show it. The pipeline copies these into the report's pipeline notes.</para>
    /// </summary>
    public IReadOnlyList<string> ReaderNotes { get; init; } = ReaderNotes ?? [];
}
