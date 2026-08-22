namespace RocketWiki.Importer.Tests;

/// <summary>
/// A configurable stand-in for the real import pipeline's id map, used to prove that
/// <see cref="ConfluenceStorageConverter"/> resolves links/attachments purely through
/// <see cref="IPageIdResolver"/> and reports (rather than throws on) unresolved references.
/// </summary>
public sealed class FakePageIdResolver : IPageIdResolver
{
    private readonly Dictionary<string, string> _pagesByTitle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _attachmentsByFileName = new(StringComparer.OrdinalIgnoreCase);

    public FakePageIdResolver WithPage(string title, string id)
    {
        _pagesByTitle[title] = id;
        return this;
    }

    public FakePageIdResolver WithAttachment(string fileName, string id)
    {
        _attachmentsByFileName[fileName] = id;
        return this;
    }

    public bool TryResolvePage(ConfluencePageReference reference, out string pageId)
    {
        if (reference.PageTitle is not null && _pagesByTitle.TryGetValue(reference.PageTitle, out var id))
        {
            pageId = id;
            return true;
        }

        pageId = string.Empty;
        return false;
    }

    public bool TryResolveAttachment(ConfluenceAttachmentReference reference, out string attachmentId)
    {
        if (_attachmentsByFileName.TryGetValue(reference.FileName, out var id))
        {
            attachmentId = id;
            return true;
        }

        attachmentId = string.Empty;
        return false;
    }
}
