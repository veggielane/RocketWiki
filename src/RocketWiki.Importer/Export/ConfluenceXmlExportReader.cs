using System.Globalization;
using System.IO.Compression;
using RocketWiki.Importer.Export.Internal;

namespace RocketWiki.Importer.Export;

/// <summary>
/// Reads Confluence's "XML" full-space-export archive (Space Tools → Content Tools →
/// Export → XML): a zip containing <c>entities.xml</c> — a generic Hibernate-style object
/// graph of <c>Space</c>, <c>Page</c>, <c>BodyContent</c>, <c>Attachment</c>, and user
/// objects — plus the attachment binaries themselves under <c>attachments/</c>.
/// </summary>
/// <remarks>
/// <b>This has not been run against a real Confluence export.</b> No Confluence instance
/// or sample export was available while building this importer (see the migration
/// converter's own report for the same caveat about hand-written fixtures vs. real
/// content). The object/property/collection shape and the class and property names below
/// (<c>Page.bodyContents</c>, <c>BodyContent.bodyType</c>, <c>Attachment.fileName</c>,
/// the <c>attachments/{id}/...</c> archive layout) reflect the documented, long-stable
/// structure of this export format, but property names have drifted slightly across
/// Confluence versions before and could again. design.md §16's trial import against a
/// real space export is exactly the step that proves or corrects the specifics here —
/// treat this class as the first draft that step is meant to validate, not a verified
/// implementation. See <c>RUNBOOK.md</c> for the specific assumptions a trial import
/// needs to confirm, comment/label resolution most of all — those are the least certain
/// part of this reader, modeled from documented shape with no way to verify which of two
/// or three plausible property names a real export actually uses.
/// </remarks>
public sealed class ConfluenceXmlExportReader : IConfluenceSpaceExportReader
{
    /// <summary>
    /// Opens the export file and reads it, handing ownership of the file handle to the
    /// returned <see cref="ConfluenceSpaceExport"/> — dispose that, and only that.
    ///
    /// <para><b>Prefer this over the stream overload.</b> The returned export hands out
    /// lazy <c>OpenContent</c> closures that reopen zip entries during pass 2, so the
    /// underlying stream has to stay open until the export is disposed. Passing a stream
    /// in makes that lifetime the caller's to get right, and the CLI got it wrong in the
    /// one way that costs the most: a <c>using var</c> around the read closed the file at
    /// the end of that block, so the first attachment threw ObjectDisposedException —
    /// after every page had already been committed and before the report was written.
    /// Every real (non-dry-run) import with an attachment died there. With the file opened
    /// here there is no second lifetime for a caller to hold.</para>
    /// </summary>
    /// <exception cref="ConfluenceExportFormatException">The file is not a readable zip,
    /// or is not a Confluence space export. The stream is closed before this propagates,
    /// so a rejected path leaks no handle.</exception>
    public ConfluenceSpaceExport Read(string exportPath)
    {
        var stream = File.OpenRead(exportPath);
        try
        {
            return Read(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads an already-open stream. <b>The returned export takes ownership</b> — the
    /// archive is built with <c>leaveOpen: false</c>, so disposing the export disposes
    /// this stream too, and the caller must not dispose it separately. See the path
    /// overload above, which removes the question.
    /// </summary>
    public ConfluenceSpaceExport Read(Stream exportZip)
    {
        var archive = new ZipArchive(exportZip, ZipArchiveMode.Read, leaveOpen: false);

        var entitiesEntry = archive.GetEntry("entities.xml")
            ?? throw new ConfluenceExportFormatException(
                "The archive has no entities.xml at its root - this doesn't look like a Confluence XML space export.");

        EntityGraph graph;
        using (var entitiesStream = entitiesEntry.Open())
        {
            graph = EntityGraph.Parse(entitiesStream);
        }

        var spaceObjects = graph.ObjectsOfClass("Space").ToList();
        if (spaceObjects.Count == 0)
        {
            throw new ConfluenceExportFormatException(
                "entities.xml contains no Space object. Expected exactly one <object class=\"Space\"> element - " +
                "this reader targets a single-space XML export (Space Tools -> Content Tools -> Export -> XML), " +
                "not a full site backup.");
        }

        if (spaceObjects.Count > 1)
        {
            throw new ConfluenceExportFormatException(
                $"entities.xml contains {spaceObjects.Count} Space objects, but this reader expects exactly one. " +
                "A multi-space export (or a full site backup) is not supported - export one space at a time.");
        }

        var spaceObject = spaceObjects[0];
        var spaceKey = graph.GetScalar(spaceObject, "key")
            ?? throw new ConfluenceExportFormatException(
                "The Space object has no 'key' property. This reader was built against the storage shape " +
                "documented for Confluence Server/Data Center XML exports (property name \"key\") and has not " +
                "been run against a real export - if a real one lacks this property, please share a redacted " +
                "sample so the property name can be corrected.");
        var spaceName = graph.GetScalar(spaceObject, "name") ?? spaceKey;
        var spaceDescription = graph.GetScalar(spaceObject, "description");

        var commentsByPageId = ResolveAllComments(graph);
        var restrictionsByPageId = ResolveAllContentRestrictions(graph);

        var pages = new List<ConfluenceExportPage>();
        foreach (var pageObject in graph.ObjectsOfClass("Page"))
        {
            // Confluence records superseded versions and unpublished drafts as their own
            // Page objects too; only "current" content should become a live page here.
            var status = graph.GetScalar(pageObject, "contentStatus");
            if (status is not null && !string.Equals(status, "current", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = graph.GetScalar(pageObject, "title") ?? $"Untitled page {pageObject.Id}";
            var parentId = graph.GetReferenceId(pageObject, "parent");
            var storageBody = ResolveStorageBody(graph, graph.GetCollectionIds(pageObject, "bodyContents"));
            var author = ResolveAuthor(graph, graph.GetReferenceId(pageObject, "creator"));
            var createdAt = ParseConfluenceDate(graph.GetScalar(pageObject, "creationDate"));

            var attachments = graph.GetCollectionIds(pageObject, "attachments")
                .Select(graph.ById)
                .Where(a => a is not null)
                .Select(a => BuildAttachment(graph, a!, archive))
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList();

            var comments = commentsByPageId.GetValueOrDefault(pageObject.Id, []);
            var labels = ResolveLabels(graph, pageObject);
            var restrictions = restrictionsByPageId.GetValueOrDefault(pageObject.Id, []);

            pages.Add(new ConfluenceExportPage(
                pageObject.Id, parentId, title, storageBody, author, createdAt, attachments, comments, labels, restrictions));
        }

        var spacePermissions = ResolveSpacePermissions(graph, spaceObject.Id);
        var space = new ConfluenceExportSpace(spaceKey, spaceName, spaceDescription, pages, spacePermissions);
        return new ConfluenceSpaceExport(archive, space);
    }

    /// <summary>
    /// Groups every Comment object by the page it belongs to, computed once for the whole
    /// export rather than per page (a Comment's owning page and, for a reply, its parent
    /// comment are the same regardless of which page we're currently looking at).
    /// </summary>
    /// <remarks>
    /// Assumed shape: a Comment carries an <c>owner</c> (falling back to <c>content</c> or
    /// <c>page</c>) reference to the <c>Page</c> it is attached to, present on every
    /// comment regardless of thread depth; <c>parent</c> is present only on a threaded
    /// reply and references another <c>Comment</c>. This is the least certain part of this
    /// reader (see the class remarks) — a comment whose owning page can't be determined is
    /// dropped rather than guessed at.
    /// </remarks>
    private static Dictionary<string, List<ConfluenceExportComment>> ResolveAllComments(EntityGraph graph)
    {
        var byPageId = new Dictionary<string, List<ConfluenceExportComment>>(StringComparer.Ordinal);

        foreach (var commentObject in graph.ObjectsOfClass("Comment"))
        {
            var status = graph.GetScalar(commentObject, "contentStatus");
            if (status is not null && !string.Equals(status, "current", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ownerRef = graph.GetReferenceId(commentObject, "owner")
                ?? graph.GetReferenceId(commentObject, "content")
                ?? graph.GetReferenceId(commentObject, "page");
            string? owningPageId = ownerRef is not null && graph.ById(ownerRef)?.Class == "Page" ? ownerRef : null;

            string? parentCommentId = null;
            var parentRef = graph.GetReferenceId(commentObject, "parent");
            if (parentRef is not null)
            {
                var parentObject = graph.ById(parentRef);
                if (parentObject?.Class == "Comment")
                {
                    parentCommentId = parentRef;
                }
                else if (parentObject?.Class == "Page")
                {
                    owningPageId ??= parentRef;
                }
            }

            if (owningPageId is null)
            {
                continue; // can't place this comment anywhere - dropped, not guessed at
            }

            var body = ResolveStorageBody(graph, graph.GetCollectionIds(commentObject, "bodyContents"));
            var author = ResolveAuthor(graph, graph.GetReferenceId(commentObject, "creator"));
            var createdAt = ParseConfluenceDate(graph.GetScalar(commentObject, "creationDate"));

            var comment = new ConfluenceExportComment(commentObject.Id, parentCommentId, body, author, createdAt);
            (byPageId.TryGetValue(owningPageId, out var list) ? list : byPageId[owningPageId] = []).Add(comment);
        }

        return byPageId;
    }

    /// <summary>
    /// Assumed shape: either a Page has a direct <c>labels</c> collection of <c>Label</c>
    /// objects, or an indirect <c>labellings</c> collection of join objects each carrying
    /// a <c>label</c> reference — both are checked since Confluence has used both shapes.
    /// A namespaced label name ("global:my-tag") is reduced to its plain tag text, since
    /// data-model.md's Label has no namespace concept.
    /// </summary>
    /// <summary>
    /// Space-level permissions (design.md §13: reported, never translated). Every
    /// <c>SpacePermission</c> is read, including types this reader has never heard of —
    /// the whole point is to show an admin what the source actually allowed, and a
    /// permission dropped for being unrecognised is exactly the one worth seeing.
    /// </summary>
    /// <remarks>
    /// Assumed shape, same caveat as the rest of this reader: <c>SpacePermission</c>
    /// objects carry <c>type</c> plus either a <c>group</c>/<c>groupName</c> scalar or a
    /// user reference (<c>userSubject</c>, or the older <c>userName</c> scalar), and a
    /// <c>space</c> reference back to the space they govern. Objects with no space
    /// reference at all are kept — a single-space export has only one space, and
    /// dropping a permission because a property name drifted is the failure mode this
    /// finding is about. Objects belonging to a DIFFERENT space are excluded.
    /// </remarks>
    private static IReadOnlyList<ConfluenceExportPermission> ResolveSpacePermissions(EntityGraph graph, string spaceObjectId)
    {
        var permissions = new List<ConfluenceExportPermission>();

        foreach (var permissionObject in graph.ObjectsOfClass("SpacePermission"))
        {
            var owningSpaceId = graph.GetReferenceId(permissionObject, "space");
            if (owningSpaceId is not null && !string.Equals(owningSpaceId, spaceObjectId, StringComparison.Ordinal))
            {
                continue;
            }

            permissions.Add(ReadPermission(graph, permissionObject));
        }

        return permissions;
    }

    /// <summary>
    /// Page restrictions, grouped by the page they restrict. Confluence models these as a
    /// <c>ContentPermissionSet</c> (one per content per action, carrying the action in its
    /// own <c>type</c>) owning a collection of <c>ContentPermission</c> entries (one per
    /// subject). Both levels are read: the set supplies the action when an individual
    /// entry omits it, which is the common shape.
    /// </summary>
    /// <remarks>
    /// A restriction whose owning page cannot be determined is <b>not</b> dropped the way
    /// an unplaceable comment is. A lost comment is lost content; a lost restriction is a
    /// silent report that a page was open when it was not, which is the over-open
    /// direction design.md §13 calls the worse of the two. Those entries are attached to
    /// the empty page id and surface as space-level "unattributed" lines in the report.
    /// </remarks>
    private static Dictionary<string, List<ConfluenceExportPermission>> ResolveAllContentRestrictions(EntityGraph graph)
    {
        var byPageId = new Dictionary<string, List<ConfluenceExportPermission>>(StringComparer.Ordinal);

        foreach (var setObject in graph.ObjectsOfClass("ContentPermissionSet"))
        {
            var setType = graph.GetScalar(setObject, "type");
            var owningContentId = graph.GetReferenceId(setObject, "owningContent")
                ?? graph.GetReferenceId(setObject, "content")
                ?? graph.GetReferenceId(setObject, "page")
                ?? string.Empty;

            var entryIds = graph.GetCollectionIds(setObject, "contentPermissions");
            if (entryIds.Count == 0)
            {
                // A set with no entries still says something: Confluence writes one when
                // a page is restricted. Record it so the page shows as restricted rather
                // than silently clean.
                Add(owningContentId, new ConfluenceExportPermission(setType ?? "restricted", null, null));
                continue;
            }

            foreach (var entryId in entryIds)
            {
                if (graph.ById(entryId) is not { } entryObject)
                {
                    continue;
                }

                var entry = ReadPermission(graph, entryObject);
                Add(owningContentId, entry.Type.Length == 0 ? entry with { Type = setType ?? "restricted" } : entry);
            }
        }

        return byPageId;

        void Add(string pageId, ConfluenceExportPermission permission)
        {
            (byPageId.TryGetValue(pageId, out var list) ? list : byPageId[pageId] = []).Add(permission);
        }
    }

    /// <summary>
    /// Reads one permission object's type and subject. The subject is resolved to a
    /// human-readable name where the export gives one (a user reference is looked up for
    /// an email or full name), because an admin re-applying these needs to recognise the
    /// people; where it does not, the raw key is reported as-is rather than as "unknown".
    /// </summary>
    private static ConfluenceExportPermission ReadPermission(EntityGraph graph, EntityObject permissionObject)
    {
        var type = graph.GetScalar(permissionObject, "type") ?? string.Empty;

        var group = graph.GetScalar(permissionObject, "group") ?? graph.GetScalar(permissionObject, "groupName");
        if (!string.IsNullOrWhiteSpace(group))
        {
            return new ConfluenceExportPermission(type, "group", group);
        }

        var userRef = graph.GetReferenceId(permissionObject, "userSubject")
            ?? graph.GetReferenceId(permissionObject, "user");
        if (userRef is not null)
        {
            var user = graph.ById(userRef);
            var name = user is null
                ? null
                : graph.GetScalar(user, "email") ?? graph.GetScalar(user, "fullName") ?? graph.GetScalar(user, "name")
                    ?? graph.GetScalar(user, "lowerName");
            return new ConfluenceExportPermission(type, "user", name ?? userRef);
        }

        var userName = graph.GetScalar(permissionObject, "userName") ?? graph.GetScalar(permissionObject, "userKey");
        if (!string.IsNullOrWhiteSpace(userName))
        {
            return new ConfluenceExportPermission(type, "user", userName);
        }

        // Confluence writes a space permission with no subject to mean "anonymous access".
        // Reported as such: it is the single most consequential line a migration report
        // can carry, since RocketWiki has no anonymous access at all (design.md non-goals).
        return new ConfluenceExportPermission(type, "anonymous", null);
    }

    private static IReadOnlyList<string> ResolveLabels(EntityGraph graph, EntityObject pageObject)
    {
        var names = new List<string>();

        foreach (var labelId in graph.GetCollectionIds(pageObject, "labels"))
        {
            var labelName = graph.ById(labelId) is { } labelObject ? graph.GetScalar(labelObject, "name") : null;
            if (labelName is not null)
            {
                names.Add(NormalizeLabelName(labelName));
            }
        }

        foreach (var labellingId in graph.GetCollectionIds(pageObject, "labellings"))
        {
            if (graph.ById(labellingId) is not { } labelling)
            {
                continue;
            }

            var labelRef = graph.GetReferenceId(labelling, "label");
            var labelName = labelRef is not null && graph.ById(labelRef) is { } labelObject
                ? graph.GetScalar(labelObject, "name")
                : null;
            if (labelName is not null)
            {
                names.Add(NormalizeLabelName(labelName));
            }
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string NormalizeLabelName(string rawName)
    {
        var colonIndex = rawName.IndexOf(':');
        return colonIndex > 0 && colonIndex < rawName.Length - 1 ? rawName[(colonIndex + 1)..] : rawName;
    }

    private static string ResolveStorageBody(EntityGraph graph, IReadOnlyList<string> bodyContentIds)
    {
        // A page can carry more than one BodyContent across format migrations Confluence
        // has made over the years; bodyType 2 is storage format (what
        // ConfluenceStorageConverter understands) and is preferred. If none is explicitly
        // typed, the first BodyContent found is used as a best effort.
        EntityObject? fallback = null;
        foreach (var id in bodyContentIds)
        {
            var bodyContent = graph.ById(id);
            if (bodyContent is null || bodyContent.Class != "BodyContent")
            {
                continue;
            }

            fallback ??= bodyContent;
            if (graph.GetScalar(bodyContent, "bodyType") == "2")
            {
                return graph.GetScalar(bodyContent, "body") ?? string.Empty;
            }
        }

        return fallback is null ? string.Empty : graph.GetScalar(fallback, "body") ?? string.Empty;
    }

    private static ConfluenceExportAuthor? ResolveAuthor(EntityGraph graph, string? creatorId)
    {
        if (creatorId is null)
        {
            return null;
        }

        var user = graph.ById(creatorId);
        if (user is null)
        {
            return null;
        }

        var email = graph.GetScalar(user, "email");
        var name = graph.GetScalar(user, "fullName") ?? graph.GetScalar(user, "name");
        return email is null && name is null ? null : new ConfluenceExportAuthor(email, name);
    }

    private static DateTimeOffset? ParseConfluenceDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        // Confluence's export renders java.util.Date without a time zone offset, in
        // whatever zone the server ran in - there is no way to recover which, so this is
        // parsed as-is and should be treated as approximate, not authoritative.
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static ConfluenceExportAttachment? BuildAttachment(EntityGraph graph, EntityObject attachmentObject, ZipArchive archive)
    {
        var fileName = graph.GetScalar(attachmentObject, "fileName");
        if (fileName is null)
        {
            return null;
        }

        var contentType = graph.GetScalar(attachmentObject, "contentType") ?? "application/octet-stream";

        // The binary layout under attachments/ has varied across Confluence versions
        // (attachments/{attachmentId}/{version} is the modern layout); match by prefix
        // rather than an exact path so small variations don't silently drop the file.
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith($"attachments/{attachmentObject.Id}/", StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        var entryFullName = entry.FullName;
        return new ConfluenceExportAttachment(attachmentObject.Id, fileName, contentType, () =>
            archive.GetEntry(entryFullName)?.Open()
            ?? throw new ConfluenceExportFormatException(
                $"Attachment '{fileName}' (Confluence id {attachmentObject.Id}) was found in entities.xml, and its " +
                $"binary was located at '{entryFullName}' while reading the archive, but that entry is gone now " +
                "that content is actually being read. The archive may have been modified between reading and use."));
    }
}
