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
/// Known simplification, flagged rather than silently shipped: design.md §12 calls for
/// a baseline bundle with "full snapshot including revision history". This
/// implementation snapshots each live page's CURRENT state only (one PageUpsert per
/// page), not its full PageRevision history - building per-revision replay was out of
/// reach in this pass.
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
        var lines = livePages
            .Select(p => new BundleEventLine(space.Key, space.Id, SequenceNumber: 0, SyncEventType.PageUpsert, SerializePageUpsert(p), DateTime.UtcNow))
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

        var manifest = new BundleManifest(localInstanceId, bundleNumber, previousManifestHash, payloadHash, spaceRanges);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        var bundlePath = Path.Combine(outputDirectory, BundleFileName(bundleNumber));
        await using (var fileStream = new FileStream(bundlePath, FileMode.CreateNew))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "manifest.json", manifestBytes, cancellationToken);
            await WriteEntryAsync(archive, "events.ndjson", ndjsonBytes, cancellationToken);
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

    private static string SerializePageUpsert(Page page) => JsonSerializer.Serialize(
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
        },
        JsonOptions);
}
