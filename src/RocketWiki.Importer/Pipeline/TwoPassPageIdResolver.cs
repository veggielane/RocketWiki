using RocketWiki.Importer.Conversion;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Backs <see cref="ConfluenceStorageConverter"/>'s <see cref="IPageIdResolver"/> with a
/// map the import pipeline builds itself, across two passes, before converting a single
/// page body:
///
/// <list type="number">
/// <item>every page in the export is created (assigning a real RocketWiki page id);</item>
/// <item>every attachment is uploaded (assigning a real RocketWiki attachment id).</item>
/// </list>
///
/// Only once both are complete does pass three convert bodies — so a link from the first
/// page in Confluence's tree to the last one resolves correctly, regardless of which was
/// created first. Registration and lookup both accept either a Confluence content id or a
/// title, matching what <c>&lt;ri:page&gt;</c>/<c>&lt;ri:attachment&gt;</c> elements
/// actually carry in practice (real exports mix both forms).
/// </summary>
public sealed class TwoPassPageIdResolver : IPageIdResolver
{
    private readonly Dictionary<(string SpaceKey, string ContentId), string> _pagesByContentId = new();
    private readonly Dictionary<(string SpaceKey, string Title), string> _pagesByTitle = new();
    private readonly Dictionary<(string SpaceKey, string ContentId, string FileName), string> _attachmentsByContentId = new();
    private readonly Dictionary<(string SpaceKey, string Title, string FileName), string> _attachmentsByTitle = new();

    public void RegisterPage(string spaceKey, string confluenceContentId, string title, string rocketWikiPageId)
    {
        _pagesByContentId[(spaceKey, confluenceContentId)] = rocketWikiPageId;
        _pagesByTitle[(spaceKey, title)] = rocketWikiPageId;
    }

    public void RegisterAttachment(string spaceKey, string owningPageContentId, string owningPageTitle, string fileName, string rocketWikiAttachmentId)
    {
        _attachmentsByContentId[(spaceKey, owningPageContentId, fileName)] = rocketWikiAttachmentId;
        _attachmentsByTitle[(spaceKey, owningPageTitle, fileName)] = rocketWikiAttachmentId;
    }

    public bool TryResolvePage(ConfluencePageReference reference, out string pageId)
    {
        if (reference.SpaceKey is not null && reference.ContentId is not null
            && _pagesByContentId.TryGetValue((reference.SpaceKey, reference.ContentId), out var byContentId))
        {
            pageId = byContentId;
            return true;
        }

        if (reference.SpaceKey is not null && reference.PageTitle is not null
            && _pagesByTitle.TryGetValue((reference.SpaceKey, reference.PageTitle), out var byTitle))
        {
            pageId = byTitle;
            return true;
        }

        pageId = string.Empty;
        return false;
    }

    public bool TryResolveAttachment(ConfluenceAttachmentReference reference, out string attachmentId)
    {
        if (reference.SpaceKey is not null && reference.ContentId is not null
            && _attachmentsByContentId.TryGetValue((reference.SpaceKey, reference.ContentId, reference.FileName), out var byContentId))
        {
            attachmentId = byContentId;
            return true;
        }

        if (reference.SpaceKey is not null && reference.PageTitle is not null
            && _attachmentsByTitle.TryGetValue((reference.SpaceKey, reference.PageTitle, reference.FileName), out var byTitle))
        {
            attachmentId = byTitle;
            return true;
        }

        attachmentId = string.Empty;
        return false;
    }
}
