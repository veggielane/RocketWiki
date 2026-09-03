using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Sync;
using RocketWiki.Data.Telemetry;
using RocketWiki.Storage;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IBundleExportService. Lives in RocketWiki.Data because it
/// needs RocketWikiDbContext and IFileStorage; the bundle FILE FORMAT itself (manifest,
/// ndjson, blobs) is plain zip/JSON with no SQL-Server-specific mechanics, so unlike
/// SearchService this is fully exercised by the SQLite test tier end-to-end.
///
/// Bundles are written as format 2 (see <see cref="BundleFormat"/>): design.md §12's
/// "full snapshot including revision history" is real now, not a flagged simplification.
/// A baseline PageUpsert carries every PageRevision of its page; an incremental
/// PageUpsert carries the ONE revision it corresponds to. Both are attached here at
/// export time, not at journal time: revisions are immutable, so joining
/// (pageId, revisionNumber) back to PageRevisions when the bundle is built yields
/// exactly what journal time would have — and it means outbox rows journaled BEFORE
/// this format existed export with their history too, instead of shipping a
/// permanently degraded payload (mixed-era outbox streams, same reasoning as
/// <see cref="EnrichAuthorPayloadsAsync"/>).
/// </summary>
public class BundleExportService : IBundleExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RocketWikiDbContext _db;
    private readonly IFileStorage _fileStorage;

    public BundleExportService(RocketWikiDbContext db, IFileStorage fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
    }

    // design.md §15: bundle export is the one long, batch-shaped operation in this
    // service layer - a span here answers "how long did the drain take and how much went
    // in it", which no per-query span can. The output directory is deliberately not
    // tagged: it's a filesystem path from operator config, not something a dashboard
    // needs, and paths have a habit of carrying environment detail.
    public async Task<ExportedBundleInfo> ExportBaselineAsync(
        Guid spaceId, string outputDirectory, string localInstanceId, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.BundleExportBaselineSpan);
        activity?.SetTag(DataTelemetry.SpaceIdTag, spaceId);

        var info = await ExportBaselineCoreAsync(spaceId, outputDirectory, localInstanceId, cancellationToken);

        activity?.SetTag(DataTelemetry.BundleNumberTag, info.BundleNumber);
        activity?.SetTag(DataTelemetry.BundleEntryCountTag, info.EventCount);
        return info;
    }

    private async Task<ExportedBundleInfo> ExportBaselineCoreAsync(
        Guid spaceId, string outputDirectory, string localInstanceId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: an archived space can still legitimately need a baseline
        // (exported-ness and archival are independent) - the two checks below are about
        // sync OWNERSHIP, not visibility.
        var space = await _db.Spaces.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Space {spaceId} not found.");

        // design.md §12's two conditions for emitting sync content, enforced HERE rather
        // than only in the CLI that happens to be the sole caller today.
        //
        // The incremental path has never had this problem: SyncOutboxWriter owns the gate,
        // so nothing can journal an event for a space that should not emit one. The
        // baseline path put the same rule in RocketWiki.Sync's argument handling instead,
        // which means this public method - a full snapshot of a space's content, its
        // restrictions and its attachment bytes - would happily produce a bundle for a
        // REPLICA the moment a second caller appeared (an admin endpoint, a scheduled job).
        // "A replica must never emit sync events for content it doesn't own" is an
        // invariant of the export, not of one console tool's flag parsing.
        //
        // The CLI keeps its own copies: it can say WHY in an operator's language and exit
        // 2, where this can only throw. Two checks of the same rule is the intended shape -
        // the friendly one for humans, the structural one for the invariant.
        if (!space.IsExported)
        {
            throw new InvalidOperationException(
                $"Space '{space.Key}' is not flagged exported, so it must not emit a baseline bundle (design.md §12): " +
                "the outbox only journals exported spaces, so this baseline would start a stream nothing continues.");
        }

        if (!string.Equals(space.OriginInstanceId, localInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Space '{space.Key}' originates from instance '{space.OriginInstanceId}', not '{localInstanceId}'. " +
                "A replica must never emit sync content it does not own (design.md §12).");
        }

        var livePages = await _db.Pages.Where(p => p.SpaceId == spaceId).ToListAsync(cancellationToken);

        // design.md §12: the baseline is a "full snapshot including revision history".
        // Every revision of every live page rides inside that page's own PageUpsert
        // line, each with its author resolved to shadow-user-creatable identity (batched
        // - one Users query for the whole space, same discipline as
        // EnrichAuthorPayloadsAsync).
        var livePageIds = livePages.Select(p => p.Id).ToList();
        var revisionsByPage = (await _db.PageRevisions
                .Where(r => livePageIds.Contains(r.PageId))
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PageId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.RevisionNumber).ToList());

        var authorIds = revisionsByPage.Values.SelectMany(rs => rs).Select(r => r.AuthorUserId).Distinct().ToList();
        var authorsById = authorIds.Count > 0
            ? await _db.Users.Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken)
            : new Dictionary<Guid, User>();

        // design.md §21: a baseline carries every page's protective marking inside its own
        // PageUpsert line. Without this the entire pre-existing content of a newly
        // exported space would land on the high side unmarked - the one gap that would
        // let classified content cross the boundary and arrive without its classification.
        // One query for the whole space, countries included.
        var markingsByPage = (await _db.PageMarkings
                .Include(m => m.Countries)
                .Include(m => m.Selectors)
                .Where(m => livePageIds.Contains(m.PageId))
                .ToListAsync(cancellationToken))
            .ToDictionary(m => m.PageId, m => m.ToMarking());

        var lines = livePages
            .Select(p => new BundleEventLine(
                space.Key, space.Id, SequenceNumber: 0, SyncEventType.PageUpsert,
                SerializePageUpsert(
                    p, revisionsByPage.GetValueOrDefault(p.Id, []), authorsById,
                    // A page with no marking row exports as TOP SECRET rather than as
                    // "no marking" - the same fail-closed substitution the read path
                    // makes, carried across the boundary so the high side inherits the
                    // caution rather than the gap.
                    markingsByPage.GetValueOrDefault(p.Id) ?? ProtectiveMarking.FailClosed),
                DateTime.UtcNow))
            .ToList();

        // --- Everything else that travels with content (design.md §12's table) --------
        //
        // A baseline is a "full snapshot" (§12), and for a long time it was not: it
        // carried pages and entries and nothing else, so a space flagged for export after
        // it already had content delivered that content stripped of its restrictions,
        // comments, attachments, labels and properties. Restrictions were the sharp edge
        // and are emitted first below for that reason — every other omission was a
        // completeness bug, but a page that was restricted on low landing on high with NO
        // restriction row is readable by every high-side viewer of the replica. That is
        // fail-OPEN, the one direction §12 never takes anywhere else (an unknown group
        // matches nobody; an unmarked upsert lands TOP SECRET).
        //
        // No format bump is needed and none is taken: every line below is an event type
        // the import side has always understood, in the byte-identical payload shape the
        // incremental writer produces, and baseline lines (SequenceNumber 0) are applied
        // without advancing or gap-checking the per-space sequence
        // (BundleImportService.ApplySpaceEventsAsync). An older format-2 importer reading
        // one of these bundles applies MORE of the space correctly, never less — so
        // replicas built by an earlier build stay compatible.
        //
        // Ordering within the bundle is deliberate: pages first (everything below
        // references a pageId, and the import side's FindLocal lookups resolve against
        // what earlier lines already tracked), then restrictions, then the rest.
        lines.AddRange(await BaselineRestrictionLinesAsync(space, livePageIds, cancellationToken));
        lines.AddRange(await BaselineLabelLinesAsync(space, livePageIds, cancellationToken));
        lines.AddRange(await BaselinePagePropertyLinesAsync(space, livePageIds, cancellationToken));
        lines.AddRange(await BaselineCommentLinesAsync(space, livePageIds, cancellationToken));
        lines.AddRange(await BaselineAttachmentLinesAsync(space, livePageIds, cancellationToken));

        // Entries are a page's structured content, so a baseline that carried the pages
        // and not their entries would land a replica showing forms with no records —
        // and they would only ever appear later, for whichever entries happened to change
        // after the baseline. Emitted as their own lines rather than nested inside the
        // page upsert so the import side has ONE parser for an entry, shared with the
        // incremental path.
        var entries = await _db.PageEntries
            .Include(e => e.Countries)
            .Where(e => livePageIds.Contains(e.PageId))
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        lines.AddRange(entries.Select(e => new BundleEventLine(
            space.Key, space.Id, SequenceNumber: 0, SyncEventType.PageEntry,
            SerializePageEntry(e), DateTime.UtcNow)));

        return await WriteBundleAsync(outputDirectory, localInstanceId, lines, cancellationToken);
    }

    /// <summary>
    /// Every page restriction on a live page in this space, as the same
    /// <c>{ accessRuleId, before, after }</c> payload an <c>AccessRuleChangedEvent</c>
    /// produces. <c>before</c> is null because a baseline is a snapshot, not a change —
    /// the import side reads only <c>after</c>, and a fabricated before-state would be a
    /// claim about history this bundle has no business making.
    ///
    /// <para>Space GRANTS are excluded, exactly as they are from the incremental journal
    /// (SyncOutboxWriter.Classify): §12 keeps them local because the high side decides who
    /// may read its own replica.</para>
    /// </summary>
    private async Task<List<BundleEventLine>> BaselineRestrictionLinesAsync(
        Space space, List<Guid> livePageIds, CancellationToken cancellationToken)
    {
        var restrictions = await _db.AccessRules
            .Where(r => r.Kind == AccessRuleKind.PageRestriction
                && r.PageId != null && livePageIds.Contains(r.PageId.Value))
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        return restrictions
            .Select(r => Line(space, SyncEventType.Restrictions, JsonSerializer.Serialize(
                new { accessRuleId = r.Id, before = (AccessRuleSnapshot?)null, after = r.ToSnapshot() },
                JsonOptions)))
            .ToList();
    }

    /// <summary>Every label attached to a live page, as the incremental writer's
    /// attach payload. Labels are matched by NAME on import, so nothing here depends on
    /// this instance's Label ids surviving the crossing.</summary>
    private async Task<List<BundleEventLine>> BaselineLabelLinesAsync(
        Space space, List<Guid> livePageIds, CancellationToken cancellationToken)
    {
        var pageLabels = await _db.PageLabels
            .Where(pl => livePageIds.Contains(pl.PageId))
            .Select(pl => new { pl.PageId, LabelName = pl.Label!.Name })
            .OrderBy(pl => pl.PageId).ThenBy(pl => pl.LabelName)
            .ToListAsync(cancellationToken);

        return pageLabels
            .Select(pl => Line(space, SyncEventType.Labels, JsonSerializer.Serialize(
                new { pageId = pl.PageId, labelName = pl.LabelName, action = "attach" }, JsonOptions)))
            .ToList();
    }

    /// <summary>Every property value on a live page (design.md §20.4). The key's NAME
    /// crosses, never this instance's registry row id — the receiving instance
    /// finds-or-creates the key by normalized name, exactly as it does for an incremental
    /// property event.</summary>
    private async Task<List<BundleEventLine>> BaselinePagePropertyLinesAsync(
        Space space, List<Guid> livePageIds, CancellationToken cancellationToken)
    {
        var properties = await _db.PageProperties
            .Where(p => livePageIds.Contains(p.PageId))
            .Select(p => new { p.PageId, KeyName = p.PropertyKey!.Key, p.Value })
            .OrderBy(p => p.PageId).ThenBy(p => p.KeyName)
            .ToListAsync(cancellationToken);

        return properties
            .Select(p => Line(space, SyncEventType.PageProperties, JsonSerializer.Serialize(
                new { pageId = p.PageId, key = p.KeyName, value = p.Value, action = "set" }, JsonOptions)))
            .ToList();
    }

    /// <summary>
    /// Every comment on a live page, authors resolved to shadow-user-creatable identity
    /// the same way <see cref="EnrichAuthorPayloadsAsync"/> resolves an incremental one.
    ///
    /// <para><b>Tombstones included, deliberately</b>, unlike attachments below. A
    /// deleted comment is a row, not an absence (design.md §5/§6.4.2 — "deletion is a
    /// tombstone, never a row removal"), and dropping it would break the thread: a live
    /// reply to a deleted comment carries a ParentCommentId FK that would have nothing to
    /// point at on the high side. Ordered parents-before-children for the same reason the
    /// pages go first.</para>
    /// </summary>
    private async Task<List<BundleEventLine>> BaselineCommentLinesAsync(
        Space space, List<Guid> livePageIds, CancellationToken cancellationToken)
    {
        var comments = await _db.Comments
            .Where(c => livePageIds.Contains(c.PageId))
            .OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        if (comments.Count == 0)
        {
            return [];
        }

        var authorsById = await LoadUsersAsync(comments.Select(c => c.AuthorUserId), cancellationToken);

        // A reply can only be imported once its parent row exists, and CreatedAtUtc order
        // gives that for anything created through the product - but a bundle must not
        // depend on clock ordering for referential integrity, so parents are hoisted
        // explicitly.
        return ParentsFirst(comments)
            .Select(c => Line(space, SyncEventType.Comment, WithAuthor(
                new
                {
                    commentId = c.Id,
                    pageId = c.PageId,
                    parentCommentId = c.ParentCommentId,
                    body = c.Body,
                    authorUserId = c.AuthorUserId,
                    isDeleted = c.IsDeleted,
                },
                authorsById.GetValueOrDefault(c.AuthorUserId))))
            .ToList();
    }

    /// <summary>
    /// Every LIVE attachment on a live page, in the incremental writer's payload shape.
    /// <see cref="WriteBlobsAsync"/> then packs their bytes automatically, since it keys
    /// off Attachment-typed lines — which is why a baseline used to ship no blobs at all.
    ///
    /// <para>Soft-deleted attachments are excluded (the global query filter does it), and
    /// that asymmetry with comments above is intentional: nothing references an attachment
    /// by FK the way a reply references its parent, and shipping the BYTES of content
    /// somebody deleted across a one-way boundary that cannot take them back is the wrong
    /// default. A baseline is the live state; a deletion that happened before it simply
    /// never crossed.</para>
    /// </summary>
    private async Task<List<BundleEventLine>> BaselineAttachmentLinesAsync(
        Space space, List<Guid> livePageIds, CancellationToken cancellationToken)
    {
        var attachments = await _db.Attachments
            .Where(a => livePageIds.Contains(a.PageId))
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        if (attachments.Count == 0)
        {
            return [];
        }

        var uploadersById = await LoadUsersAsync(attachments.Select(a => a.UploadedByUserId), cancellationToken);

        return attachments
            .Select(a => Line(space, SyncEventType.Attachment, WithAuthor(
                new
                {
                    attachmentId = a.Id,
                    pageId = a.PageId,
                    fileName = a.FileName,
                    contentType = a.ContentType,
                    sizeBytes = a.SizeBytes,
                    contentHash = Convert.ToHexString(a.ContentHash),
                    isDeleted = a.IsDeleted,
                    uploadedByUserId = a.UploadedByUserId,
                },
                uploadersById.GetValueOrDefault(a.UploadedByUserId))))
            .ToList();
    }

    /// <summary>Comments ordered so every parent precedes its children, whatever the
    /// timestamps say. A cycle or a parent outside this space (neither reachable through
    /// the product) degrades to "emit the leftovers in their original order" rather than
    /// looping.</summary>
    private static IEnumerable<Comment> ParentsFirst(List<Comment> comments)
    {
        var emitted = new HashSet<Guid>();
        var remaining = new List<Comment>(comments);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(c => c.ParentCommentId is null || emitted.Contains(c.ParentCommentId.Value))
                .ToList();
            if (ready.Count == 0)
            {
                foreach (var leftover in remaining)
                {
                    yield return leftover;
                }

                yield break;
            }

            foreach (var comment in ready)
            {
                emitted.Add(comment.Id);
                yield return comment;
            }

            remaining.RemoveAll(c => emitted.Contains(c.Id));
        }
    }

    /// <summary>The generic authorDisplayName/authorEmail keys the import side's
    /// EnsureShadowUserAsync reads, whatever kind of event carried them (design.md §12:
    /// "authors arrive as shadow users"). Same keys EnrichAuthorPayloadsAsync writes onto
    /// an incremental comment or attachment, so one import path serves both.</summary>
    private static string WithAuthor(object payload, User? author)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions)!.AsObject();
        node["authorDisplayName"] = author?.DisplayName;
        node["authorEmail"] = author?.Email;
        return node.ToJsonString(JsonOptions);
    }

    /// <summary>One Users query for a whole baseline section, not one per row — the same
    /// discipline EnrichAuthorPayloadsAsync applies to a drain.</summary>
    private async Task<Dictionary<Guid, User>> LoadUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await _db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
    }

    /// <summary>A baseline line: sequence number 0, which is what tells the import side
    /// to apply it without advancing or gap-checking the per-space sequence.</summary>
    private static BundleEventLine Line(Space space, SyncEventType eventType, string payloadJson) =>
        new(space.Key, space.Id, SequenceNumber: 0, eventType, payloadJson, DateTime.UtcNow);

    /// <summary>
    /// The shape an entry crosses in — identical to the incremental writer's, so the
    /// import side parses one format. Every field is written even when null: the import
    /// assigns what it reads, so an omitted field would be read as a cleared one. That is
    /// the bug page icons shipped with.
    /// </summary>
    private static string SerializePageEntry(PageEntry entry) => JsonSerializer.Serialize(
        new
        {
            entryId = entry.Id,
            pageId = entry.PageId,
            collection = entry.Collection,
            data = entry.Data,
            version = entry.Version,
            level = ProtectiveMarking.LevelWireName(entry.Level),
            eyesOnly = entry.Countries.Select(c => c.CountryValue).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            prefix = entry.Prefix,
            isDeleted = entry.IsDeleted,
        },
        JsonOptions);

    public async Task<ExportedBundleInfo?> ExportIncrementalAsync(
        string outputDirectory, string localInstanceId, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.BundleExportIncrementalSpan);

        var info = await ExportIncrementalCoreAsync(outputDirectory, localInstanceId, cancellationToken);

        // Nothing pending is a normal outcome, not an error - record it as an empty
        // drain so a dashboard can tell "ran, had nothing" from "never ran".
        activity?.SetTag(DataTelemetry.BundleEntryCountTag, info?.EventCount ?? 0);
        if (info is not null)
        {
            activity?.SetTag(DataTelemetry.BundleNumberTag, info.BundleNumber);
        }

        return info;
    }

    private async Task<ExportedBundleInfo?> ExportIncrementalCoreAsync(
        string outputDirectory, string localInstanceId, CancellationToken cancellationToken)
    {
        var pendingEvents = await _db.SyncOutboxEvents
            .Where(e => e.ExportedInBundle == null)
            .OrderBy(e => e.SpaceId).ThenBy(e => e.SequenceNumber)
            .ToListAsync(cancellationToken);

        if (pendingEvents.Count == 0)
        {
            return null;
        }

        var spaceIds = pendingEvents.Select(e => e.SpaceId).Distinct().ToList();
        var spaceKeysById = await _db.Spaces
            .Where(s => spaceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Key, cancellationToken);

        var enrichedPayloads = await EnrichAuthorPayloadsAsync(pendingEvents, cancellationToken);
        enrichedPayloads = await EnrichRevisionPayloadsAsync(pendingEvents, enrichedPayloads, cancellationToken);
        enrichedPayloads = await EnrichMarkingPayloadsAsync(pendingEvents, enrichedPayloads, cancellationToken);

        var lines = pendingEvents
            .Select(e => new BundleEventLine(spaceKeysById[e.SpaceId], e.SpaceId, e.SequenceNumber, e.EventType, enrichedPayloads[e.Id], e.CreatedAtUtc))
            .ToList();

        var info = await WriteBundleAsync(outputDirectory, localInstanceId, lines, cancellationToken);

        foreach (var outboxEvent in pendingEvents)
        {
            outboxEvent.ExportedInBundle = info.BundleNumber;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return info;
    }

    /// <summary>
    /// design.md §12: "Authors arrive as shadow users (name/email from the event...)".
    /// Comment payloads carry a raw AuthorUserId, and Attachment payloads a raw
    /// UploadedByUserId - both meaningless as an identity on the high side beyond the
    /// Guid itself. This enriches either kind with that user's current DisplayName/Email
    /// from THIS instance's own Users table under the SAME generic authorDisplayName/
    /// authorEmail keys, batched into one query rather than one lookup per event, so the
    /// import side's EnsureShadowUserAsync doesn't need to know which event type it came
    /// from.
    /// </summary>
    private async Task<Dictionary<long, string>> EnrichAuthorPayloadsAsync(List<SyncOutboxEvent> events, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();
        var attributableEvents = events.Where(e => e.EventType is SyncEventType.Comment or SyncEventType.Attachment).ToList();

        Dictionary<Guid, User> authorsById = new();
        if (attributableEvents.Count > 0)
        {
            var authorIds = attributableEvents
                .Select(e => TryGetGuidProperty(e.PayloadJson, AuthorIdPropertyName(e.EventType)))
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            if (authorIds.Count > 0)
            {
                authorsById = await _db.Users.Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
            }
        }

        foreach (var evt in events)
        {
            if (evt.EventType is not (SyncEventType.Comment or SyncEventType.Attachment))
            {
                result[evt.Id] = evt.PayloadJson;
                continue;
            }

            var node = JsonNode.Parse(evt.PayloadJson)?.AsObject();
            var authorId = TryGetGuidProperty(evt.PayloadJson, AuthorIdPropertyName(evt.EventType));
            if (node is not null && authorId is not null && authorsById.TryGetValue(authorId.Value, out var author))
            {
                node["authorDisplayName"] = author.DisplayName;
                node["authorEmail"] = author.Email;
                result[evt.Id] = node.ToJsonString(JsonOptions);
            }
            else
            {
                result[evt.Id] = evt.PayloadJson;
            }
        }

        return result;
    }

    /// <summary>
    /// Attaches to every incremental PageUpsert payload the ONE PageRevision it
    /// corresponds to (the payload's own pageId + revisionNumber, joined back to the
    /// immutable PageRevisions table), as a one-element <c>revisions</c> array in the
    /// same shape a baseline uses - one payload key, one import path. Done at EXPORT
    /// time, like author enrichment, and for the same two reasons: journal payloads stay
    /// lean, and outbox rows journaled before this format existed (which carry no
    /// revision data at all) still export with their history - the revision row they
    /// point at is immutable and still there. A payload whose revision row genuinely
    /// cannot be found is left untouched; the import side treats an absent
    /// <c>revisions</c> key as current-state-only, exactly like a legacy bundle.
    /// </summary>
    private async Task<Dictionary<long, string>> EnrichRevisionPayloadsAsync(
        List<SyncOutboxEvent> events, Dictionary<long, string> payloads, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<long, (Guid PageId, int RevisionNumber)>();
        foreach (var evt in events.Where(e => e.EventType == SyncEventType.PageUpsert))
        {
            using var document = JsonDocument.Parse(payloads[evt.Id]);
            var root = document.RootElement;
            if (root.TryGetProperty("pageId", out var pageIdElement) && pageIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(pageIdElement.GetString(), out var pageId)
                && root.TryGetProperty("revisionNumber", out var revisionElement) && revisionElement.ValueKind == JsonValueKind.Number)
            {
                wanted[evt.Id] = (pageId, revisionElement.GetInt32());
            }
        }

        if (wanted.Count == 0)
        {
            return payloads;
        }

        // One query for the whole drain. pageIds x revisionNumbers over-fetches the
        // (small) cross product rather than issuing a per-page query or a
        // provider-specific composite-key IN; the exact (pageId, revisionNumber) match
        // happens in memory.
        var pageIds = wanted.Values.Select(w => w.PageId).Distinct().ToList();
        var revisionNumbers = wanted.Values.Select(w => w.RevisionNumber).Distinct().ToList();
        var revisionsByKey = (await _db.PageRevisions
                .Where(r => pageIds.Contains(r.PageId) && revisionNumbers.Contains(r.RevisionNumber))
                .ToListAsync(cancellationToken))
            .ToDictionary(r => (r.PageId, r.RevisionNumber));

        var authorIds = revisionsByKey.Values.Select(r => r.AuthorUserId).Distinct().ToList();
        var authorsById = authorIds.Count > 0
            ? await _db.Users.Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken)
            : new Dictionary<Guid, User>();

        foreach (var (eventId, key) in wanted)
        {
            if (!revisionsByKey.TryGetValue(key, out var revision))
            {
                continue;
            }

            var node = JsonNode.Parse(payloads[eventId])!.AsObject();
            node["revisions"] = JsonSerializer.SerializeToNode(
                new[] { RevisionPayload(revision, authorsById.GetValueOrDefault(revision.AuthorUserId)) }, JsonOptions);
            payloads[eventId] = node.ToJsonString(JsonOptions);
        }

        return payloads;
    }

    /// <summary>
    /// Attaches the page's CURRENT protective marking to every incremental PageUpsert
    /// payload (design.md §21), in the same shape a baseline uses — one payload key, one
    /// import path.
    ///
    /// <para>Done at EXPORT time rather than journalled into the outbox row, for the same
    /// two reasons revision enrichment is: journal payloads stay lean, and outbox rows
    /// written before markings existed still export with one. That second reason is the
    /// important one here — without it, every page edited on low before this feature
    /// shipped would cross as an unmarked upsert and the high side would have to guess.
    /// It also means the marking on an upsert is the page's state at export time, not at
    /// edit time; that is the correct reading for a snapshot-replay protocol, and any
    /// marking change in between has its own PageMarking event in the same drain
    /// anyway.</para>
    ///
    /// <para>A page whose marking row is genuinely missing exports as TOP SECRET, not as
    /// an absent key: the high side must never be able to infer "unmarked" from a bundle
    /// this instance produced.</para>
    /// </summary>
    private async Task<Dictionary<long, string>> EnrichMarkingPayloadsAsync(
        List<SyncOutboxEvent> events, Dictionary<long, string> payloads, CancellationToken cancellationToken)
    {
        var pageIdsByEvent = new Dictionary<long, Guid>();
        foreach (var evt in events.Where(e => e.EventType == SyncEventType.PageUpsert))
        {
            var pageId = TryGetGuidProperty(payloads[evt.Id], "pageId");
            if (pageId is not null)
            {
                pageIdsByEvent[evt.Id] = pageId.Value;
            }
        }

        if (pageIdsByEvent.Count == 0)
        {
            return payloads;
        }

        // One query for the whole drain, countries included.
        var pageIds = pageIdsByEvent.Values.Distinct().ToList();
        var markingsByPage = (await _db.PageMarkings
                .Include(m => m.Countries)
                .Include(m => m.Selectors)
                .Where(m => pageIds.Contains(m.PageId))
                .ToListAsync(cancellationToken))
            .ToDictionary(m => m.PageId, m => m.ToMarking());

        foreach (var (eventId, pageId) in pageIdsByEvent)
        {
            var marking = markingsByPage.GetValueOrDefault(pageId) ?? ProtectiveMarking.FailClosed;
            var node = JsonNode.Parse(payloads[eventId])!.AsObject();
            node["marking"] = JsonSerializer.SerializeToNode(MarkingPayload(marking), JsonOptions);
            payloads[eventId] = node.ToJsonString(JsonOptions);
        }

        return payloads;
    }

    private static string AuthorIdPropertyName(SyncEventType eventType) =>
        eventType == SyncEventType.Attachment ? "uploadedByUserId" : "authorUserId";

    private static Guid? TryGetGuidProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guid)
            ? guid
            : null;
    }

    private async Task<ExportedBundleInfo> WriteBundleAsync(
        string outputDirectory, string localInstanceId, List<BundleEventLine> lines, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var bundleNumber = DetermineNextBundleNumber(outputDirectory);
        var previousManifestHash = await ReadPreviousManifestHashAsync(outputDirectory, bundleNumber, cancellationToken);

        var ndjsonBuilder = new StringBuilder();
        foreach (var line in lines)
        {
            var record = new NdjsonEventRecord(line.SpaceKey, line.SpaceId, line.SequenceNumber, line.EventType.ToString(), line.PayloadJson, line.CreatedAtUtc);
            ndjsonBuilder.AppendLine(JsonSerializer.Serialize(record, JsonOptions));
        }

        var ndjsonBytes = Encoding.UTF8.GetBytes(ndjsonBuilder.ToString());
        var payloadHash = Convert.ToHexString(SHA256.HashData(ndjsonBytes));

        var spaceRanges = lines
            .GroupBy(l => l.SpaceKey)
            .ToDictionary(g => g.Key, g => new SpaceEventRange(g.First().SpaceId, g.Min(l => l.SequenceNumber), g.Max(l => l.SequenceNumber), g.Count()));

        var manifest = new BundleManifest(
            localInstanceId, bundleNumber, previousManifestHash, payloadHash, spaceRanges, BundleFormat.CurrentVersion);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        var bundlePath = Path.Combine(outputDirectory, BundleFileName(bundleNumber));
        await using (var fileStream = new FileStream(bundlePath, FileMode.CreateNew))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "manifest.json", manifestBytes, cancellationToken);
            // The version-specific entry name is part of the format contract: it is what
            // makes a format-1 importer refuse this bundle loudly instead of silently
            // dropping its revision history - see BundleFormat.
            await WriteEntryAsync(archive, BundleFormat.EventsEntryName(BundleFormat.CurrentVersion), ndjsonBytes, cancellationToken);
            await WriteBlobsAsync(archive, lines, cancellationToken);
        }

        return new ExportedBundleInfo(bundleNumber, bundlePath, lines.Count);
    }

    /// <summary>Distinct content hashes referenced by Attachment-type lines, each written once even if referenced by multiple events (data-model.md: ContentHash "detects duplicates").</summary>
    private async Task WriteBlobsAsync(ZipArchive archive, List<BundleEventLine> lines, CancellationToken cancellationToken)
    {
        var attachmentIds = lines
            .Where(l => l.EventType == SyncEventType.Attachment)
            .Select(l => TryGetGuidProperty(l.PayloadJson, "attachmentId"))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (attachmentIds.Count == 0)
        {
            return;
        }

        var attachments = await _db.Attachments.IgnoreQueryFilters().Where(a => attachmentIds.Contains(a.Id)).ToListAsync(cancellationToken);
        var writtenHashes = new HashSet<string>();

        foreach (var attachment in attachments)
        {
            var hashHex = Convert.ToHexString(attachment.ContentHash);
            if (!writtenHashes.Add(hashHex))
            {
                continue; // already packed this content once
            }

            if (!await _fileStorage.ExistsAsync(attachment.StorageKey, cancellationToken))
            {
                continue; // design.md §10: a missing blob is an operational fault, not something export should crash on
            }

            var entry = archive.CreateEntry($"blobs/{hashHex}");
            await using var entryStream = entry.Open();
            await using var sourceStream = await _fileStorage.OpenReadAsync(attachment.StorageKey, cancellationToken);
            await sourceStream.CopyToAsync(entryStream, cancellationToken);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string entryName, byte[] bytes, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(bytes, cancellationToken);
    }

    private static string BundleFileName(int bundleNumber) => $"bundle-{bundleNumber:D6}.zip";

    private static int DetermineNextBundleNumber(string outputDirectory)
    {
        var existing = Directory.Exists(outputDirectory)
            ? Directory.GetFiles(outputDirectory, "bundle-*.zip")
                .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Replace("bundle-", ""), out var n) ? n : (int?)null)
                .Where(n => n is not null)
                .Select(n => n!.Value)
                .ToList()
            : new List<int>();

        return existing.Count == 0 ? 1 : existing.Max() + 1;
    }

    private static async Task<string?> ReadPreviousManifestHashAsync(string outputDirectory, int bundleNumber, CancellationToken cancellationToken)
    {
        if (bundleNumber <= 1)
        {
            return null;
        }

        var previousPath = Path.Combine(outputDirectory, BundleFileName(bundleNumber - 1));
        if (!File.Exists(previousPath))
        {
            throw new InvalidOperationException(
                $"Cannot compute the manifest chain: bundle {bundleNumber - 1} is missing from '{outputDirectory}'.");
        }

        using var archive = ZipFile.OpenRead(previousPath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException($"Bundle {bundleNumber - 1} is missing manifest.json.");

        using var stream = manifestEntry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return Convert.ToHexString(SHA256.HashData(memory.ToArray()));
    }

    /// <summary>Baseline line: the page's current state plus its complete revision history (design.md §12).</summary>
    private static string SerializePageUpsert(
        Page page, IReadOnlyList<PageRevision> revisions, IReadOnlyDictionary<Guid, User> authorsById,
        ProtectiveMarking marking) =>
        JsonSerializer.Serialize(
            new
            {
                pageId = page.Id,
                spaceId = page.SpaceId,
                parentPageId = page.ParentPageId,
                ancestorPath = page.AncestorPath,
                slug = page.Slug,
                title = page.Title,
                // By NAME, like the marking's level below: an icon added to the enum
                // must not renumber what an existing bundle means. A replica that does
                // not know this name imports the page without an icon (§12's
                // decoration-degrades rule) rather than refusing the bundle.
                icon = page.Icon is null ? null : PageIcons.ToWireName(page.Icon.Value),
                sortOrder = page.SortOrder,
                content = page.CurrentContent,
                revisionNumber = page.CurrentRevisionNumber,
                revisions = revisions.Select(r => RevisionPayload(r, authorsById.GetValueOrDefault(r.AuthorUserId))).ToList(),
                marking = MarkingPayload(marking),
            },
            JsonOptions);

    /// <summary>The shape a marking crosses in, shared by the baseline's PageUpsert and
    /// the incremental enrichment below so the import side has exactly one parser.</summary>
    private static object MarkingPayload(ProtectiveMarking marking) => new
    {
        level = ProtectiveMarking.LevelWireName(marking.Level),
        eyesOnly = marking.EyesOnly,
        // design.md §21.10: an object keyed by category, so "one value per category" is
        // structural on the wire, and ALWAYS present ({} when none) so that a missing key
        // is unambiguously a pre-selector bundle rather than a cleared set. Crosses
        // verbatim; a category the high side has not configured matches nobody (§12).
        selectors = marking.Selectors.ToDictionary(s => s.Category, s => s.Value, StringComparer.Ordinal),
        // Presentational, but it crosses: a replica must render the same marking string
        // as its origin (design.md §21.12). Null is a legal value and stays null.
        prefix = marking.Prefix,
    };

    /// <summary>
    /// One entry of a PageUpsert payload's <c>revisions</c> array. Carries the same
    /// generic authorDisplayName/authorEmail keys comments and attachments use, so the
    /// import side's EnsureShadowUserAsync handles a revision author identically
    /// (design.md §12: "Authors arrive as shadow users"). A missing local User row (only
    /// reachable with pathological data) degrades to identity-less - the import side
    /// then creates the shadow user from the Guid alone.
    /// </summary>
    private static object RevisionPayload(PageRevision revision, User? author) => new
    {
        revisionNumber = revision.RevisionNumber,
        title = revision.Title,
        content = revision.Content,
        editSummary = revision.EditSummary,
        authorUserId = revision.AuthorUserId,
        authorDisplayName = author?.DisplayName,
        authorEmail = author?.Email,
        createdAtUtc = revision.CreatedAtUtc,
    };
}
