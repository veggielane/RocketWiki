namespace RocketWiki.Importer.Conversion;

/// <summary>
/// Identifies a Confluence page referenced by an <c>&lt;ac:link&gt;</c>/<c>&lt;ri:page&gt;</c> element.
/// At least one of <paramref name="PageTitle"/> or <paramref name="ContentId"/> is normally present;
/// Confluence exports vary in which they record.
/// </summary>
public sealed record ConfluencePageReference(string? SpaceKey, string? PageTitle, string? ContentId);

/// <summary>
/// Identifies a Confluence attachment referenced by an <c>&lt;ri:attachment&gt;</c> element,
/// either directly (belongs to the page being converted) or via a nested <c>&lt;ri:page&gt;</c>
/// pointing at a different source page.
/// </summary>
public sealed record ConfluenceAttachmentReference(string? SpaceKey, string? PageTitle, string? ContentId, string FileName);

/// <summary>
/// Resolves Confluence page and attachment references to RocketWiki ids.
/// </summary>
/// <remarks>
/// The converter runs standalone, before pages and attachments have been created in
/// RocketWiki, so it cannot know real ids. The import pipeline (not part of this library)
/// is expected to supply an implementation backed by whatever id-assignment strategy it
/// uses (e.g. a pre-computed title/content-id -&gt; new-page-id map built in a first pass
/// over the whole space export). Tests supply a fake.
/// </remarks>
public interface IPageIdResolver
{
    /// <summary>
    /// Attempts to resolve a Confluence page reference to a RocketWiki page id.
    /// Returns <see langword="false"/> if the target page is unknown (e.g. it was
    /// never exported, or was excluded from the migration) — the converter treats
    /// that as a lossy, reportable condition rather than an error.
    /// </summary>
    bool TryResolvePage(ConfluencePageReference reference, out string pageId);

    /// <summary>
    /// Attempts to resolve a Confluence attachment reference to a RocketWiki attachment id.
    /// Returns <see langword="false"/> if the attachment is unknown.
    /// </summary>
    bool TryResolveAttachment(ConfluenceAttachmentReference reference, out string attachmentId);
}
