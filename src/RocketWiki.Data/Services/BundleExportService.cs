using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
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
        var space = await _db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Space {spaceId} not found.");

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

        var lines = livePages
            .Select(p => new BundleEventLine(
                space.Key, space.Id, SequenceNumber: 0, SyncEventType.PageUpsert,
                SerializePageUpsert(p, revisionsByPage.GetValueOrDefault(p.Id, []), authorsById), DateTime.UtcNow))
            .ToList();

        return await WriteBundleAsync(outputDirectory, localInstanceId, lines, cancellationToken);
    }

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
    private static string SerializePageUpsert(Page page, IReadOnlyList<PageRevision> revisions, IReadOnlyDictionary<Guid, User> authorsById) =>
        JsonSerializer.Serialize(
            new
            {
                pageId = page.Id,
                spaceId = page.SpaceId,
                parentPageId = page.ParentPageId,
                ancestorPath = page.AncestorPath,
                slug = page.Slug,
                title = page.Title,
                sortOrder = page.SortOrder,
                content = page.CurrentContent,
                revisionNumber = page.CurrentRevisionNumber,
                revisions = revisions.Select(r => RevisionPayload(r, authorsById.GetValueOrDefault(r.AuthorUserId))).ToList(),
            },
            JsonOptions);

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
