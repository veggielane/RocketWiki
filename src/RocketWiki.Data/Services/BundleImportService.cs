using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Access;
using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Core.Sync;
using RocketWiki.Data.Telemetry;
using RocketWiki.Storage;

namespace RocketWiki.Data.Services;

/// <summary>
/// EF-backed implementation of IBundleImportService. design.md §12: strictly ordered,
/// gap-refusing, idempotent, hash-chain-verified. Every check that can fail happens
/// BEFORE any entity in the bundle is applied, so a rejected bundle never partially lands.
/// </summary>
public class BundleImportService : IBundleImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RocketWikiDbContext _db;
    private readonly IFileStorage _fileStorage;
    private readonly string? _localInstanceId;
    private readonly BundleLimits _limits;

    /// <param name="localInstanceId">
    /// design.md §12: this instance's own identity, used for exactly one check — refusing a
    /// bundle that claims to originate from here (<see cref="BundleSelfOriginError"/>).
    /// Nothing else in the import consults it; a replica's read-only-ness is decided later,
    /// by comparing the stored <c>OriginInstanceId</c> against the id the API was
    /// configured with.
    ///
    /// <para>Nullable because a caller that genuinely does not know its own identity should
    /// not be forced to invent one — but the consequence is stated rather than hidden: the
    /// self-origin check cannot run, and the only guard left is the operator's own care.
    /// Every production path supplies it.</para>
    /// </param>
    /// <param name="limits">
    /// Decompression ceilings (<see cref="BundleLimits"/>). Defaulted rather than required
    /// because there is exactly one right answer in production and no caller should be
    /// choosing; the parameter exists so a test can prove the refusal with a few kilobytes
    /// instead of a few gigabytes.
    /// </param>
    public BundleImportService(
        RocketWikiDbContext db, IFileStorage fileStorage, string? localInstanceId = null, BundleLimits? limits = null)
    {
        _db = db;
        _fileStorage = fileStorage;
        _localInstanceId = localInstanceId;
        _limits = limits ?? BundleLimits.Default;
    }

    // design.md §15: the bundle path is not tagged (an operator filesystem path), and
    // neither is originInstanceId beyond what the audit row already records. What a span
    // adds over SQL Client's per-query view is the whole apply as one timed unit, plus
    // how many events it applied and whether it was a duplicate no-op.
    public async Task<PageMutationResult<ImportedBundleSummary>> ImportAsync(
        string bundleFilePath, string originInstanceId, AuditContext auditContext, CancellationToken cancellationToken = default)
    {
        using var activity = DataTelemetry.StartSpan(DataTelemetry.BundleImportSpan);

        var result = DataTelemetry.Finish(activity,
            await ImportCoreAsync(bundleFilePath, originInstanceId, auditContext, cancellationToken));
        if (result.IsSuccess)
        {
            activity?.SetTag(DataTelemetry.BundleNumberTag, result.Value.BundleNumber);
            activity?.SetTag(DataTelemetry.BundleEntryCountTag, result.Value.EventsApplied);
            activity?.SetTag(DataTelemetry.BundleDuplicateTag, result.Value.WasDuplicate);
        }

        return result;
    }

    /// <summary>
    /// Wraps the import so the decompression ceilings (<see cref="BundleLimits"/>) can be
    /// enforced deep inside a stream copy and still surface as the typed refusal every
    /// other integrity failure returns. The alternative — threading a result type through
    /// every helper that touches a zip entry — would put the check's plumbing in more
    /// places than the check itself, which is how a guard ends up skipped on the one path
    /// nobody edited.
    /// </summary>
    private async Task<PageMutationResult<ImportedBundleSummary>> ImportCoreAsync(
        string bundleFilePath, string originInstanceId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        try
        {
            return await ImportBoundedAsync(bundleFilePath, originInstanceId, auditContext, cancellationToken);
        }
        catch (Exception ex) when (ex is BundleLimitExceededException or DecompressionLimitExceededException)
        {
            // Two shapes of the same refusal: the ceilings this class checks itself (entry
            // count, declared sizes, line count) and the one BoundedReadStream enforces
            // mid-read on the bytes that actually arrive.
            return PageMutationResult<ImportedBundleSummary>.Failure(new BundleTooLargeError(ex.Message));
        }
    }

    private async Task<PageMutationResult<ImportedBundleSummary>> ImportBoundedAsync(
        string bundleFilePath, string originInstanceId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        // design.md §12, before anything is read: a bundle from THIS instance is refused.
        // Importing it would write its spaces with OriginInstanceId equal to the local id,
        // which is precisely the test Space.IsReplicaOf uses - so the "replicas are
        // read-only, always" invariant would answer false and every mirrored space would be
        // writable. The realistic way that happens is not tampering, it is two instances
        // left on the same identity (the Helm chart shipped exactly that default), so this
        // guard is about a configuration mistake, not an attack.
        if (_localInstanceId is not null
            && string.Equals(originInstanceId, _localInstanceId, StringComparison.Ordinal))
        {
            return PageMutationResult<ImportedBundleSummary>.Failure(
                new BundleSelfOriginError(_localInstanceId));
        }

        using var archive = ZipFile.OpenRead(bundleFilePath);

        // The cheapest possible refusal, and the first one: entry count and the total
        // uncompressed size the archive's own central directory declares. Both numbers
        // come from the bundle, so a bomb can lie about the second - which is why every
        // read below ALSO counts what it actually decompresses. This pass exists so the
        // obvious case is rejected without decompressing a single byte.
        CheckArchiveShape(archive);

        var manifestBytes = await ReadEntryAsync(archive, "manifest.json", _limits.MaxManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidOperationException($"'{bundleFilePath}' has an unparseable manifest.json.");

        // BundleFormat: a NEWER format than this instance understands is refused before
        // a single event byte is parsed - never partially understood. Anything at or
        // below CurrentVersion (including format-1 bundles, whose manifests predate the
        // FormatVersion field) is accepted; a format-1 bundle simply carries no revision
        // history to materialize.
        if (manifest.FormatVersion > BundleFormat.CurrentVersion)
        {
            return PageMutationResult<ImportedBundleSummary>.Failure(
                new BundleFormatUnsupportedError(manifest.FormatVersion, BundleFormat.CurrentVersion));
        }

        // design.md §12: the bundle's OWN declared origin must be the stream the operator
        // said they were importing. Every replica space, every SyncImportState position and
        // every per-space sequence on this side is keyed by `originInstanceId` - the
        // argument - and manifest.InstanceId was, until this check, read by nothing at all.
        // A bundle from instance A imported as if it came from B splices two streams into
        // one position, and the strict ordering that position exists to enforce becomes an
        // ordering over nothing. The realistic route in is a wrong --origin-instance-id on
        // a scheduled job, not forgery, which is exactly why it is worth catching.
        if (!string.Equals(manifest.InstanceId, originInstanceId, StringComparison.Ordinal))
        {
            return PageMutationResult<ImportedBundleSummary>.Failure(
                new BundleOriginMismatchError(manifest.InstanceId ?? "(absent)", originInstanceId));
        }

        var eventsEntryName = BundleFormat.EventsEntryName(manifest.FormatVersion);
        var eventsBytes = await ReadEntryAsync(archive, eventsEntryName, _limits.MaxEventsBytes, cancellationToken);
        var actualPayloadHash = Convert.ToHexString(SHA256.HashData(eventsBytes));
        if (!string.Equals(actualPayloadHash, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            return PageMutationResult<ImportedBundleSummary>.Failure(new BundlePayloadTamperedError(
                $"{eventsEntryName} hashes to {actualPayloadHash}, but the manifest declares {manifest.PayloadSha256}."));
        }

        var importState = await _db.SyncImportStates.FirstOrDefaultAsync(s => s.OriginInstanceId == originInstanceId, cancellationToken);

        // design.md §12: "Apply is idempotent, so duplicate delivery is harmless" - a
        // bundle numbered at or below the last one we applied is a no-op success, not
        // an error, and it is NOT re-applied (re-applying could resurrect content a
        // LATER, already-applied bundle deliberately moved past, e.g. a delete).
        if (importState is not null && manifest.BundleNumber <= importState.LastBundleNumber)
        {
            _db.AuditContext = auditContext;
            _db.RaiseDomainEvent(new SyncImportedEvent(originInstanceId, manifest.BundleNumber, BuildSpaceRanges(manifest), WasDuplicate: true));
            await _db.SaveChangesAsync(cancellationToken);
            return PageMutationResult<ImportedBundleSummary>.Success(new ImportedBundleSummary(manifest.BundleNumber, EventsApplied: 0, WasDuplicate: true));
        }

        var expectedBundleNumber = (importState?.LastBundleNumber ?? 0) + 1;
        if (manifest.BundleNumber != expectedBundleNumber)
        {
            // design.md §12: "if bundle 41 hasn't been applied, 42 waits."
            return PageMutationResult<ImportedBundleSummary>.Failure(new BundleGapError(expectedBundleNumber, manifest.BundleNumber));
        }

        if (!string.Equals(manifest.PreviousManifestHash, importState?.LastManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            return PageMutationResult<ImportedBundleSummary>.Failure(new BundleChainMismatchError(
                $"Bundle {manifest.BundleNumber}'s PreviousManifestHash does not match the last applied bundle's manifest hash - " +
                "a bundle may be missing, reordered, or tampered."));
        }

        // Attachment BYTES, verified before any of them is written (see the method).
        // Deliberately after the cheap manifest-level checks and after the duplicate
        // no-op above: this is the only check in the file that costs a full read of
        // every blob, so a bundle that is going to be refused for being out of order,
        // or skipped for having already been applied, is never hashed.
        var blobError = await VerifyBlobIntegrityAsync(archive, cancellationToken);
        if (blobError is not null)
        {
            return PageMutationResult<ImportedBundleSummary>.Failure(blobError);
        }

        var records = ParseEvents(eventsBytes, _limits.MaxEventLines);

        // EVERY per-space sequence is checked before ANY event is applied. This used to
        // happen inside the apply loop, which made this class's own opening claim — "every
        // check that can fail happens BEFORE any entity in the bundle is applied, so a
        // rejected bundle never partially lands" — true of the database and false of
        // storage: attachment events call _fileStorage.SaveAsync as they are applied, so a
        // gap in space B left every blob from space A written. SaveChangesAsync is never
        // reached on a refusal, so the ROWS evaporate and the blobs do not, and a
        // corrected re-import mints a fresh storage key — making the orphan permanent,
        // with no janitor to collect it (README).
        //
        // Nothing here touches the database or storage; it reads the same SyncSpaceState
        // high-water marks the apply loop would, so the verdict is identical, only earlier.
        foreach (var spaceGroup in records.GroupBy(r => r.SpaceId))
        {
            var gap = await FindSequenceGapAsync(
                spaceGroup.Key, originInstanceId, spaceGroup.OrderBy(r => r.SequenceNumber).ToList(), cancellationToken);
            if (gap is not null)
            {
                return PageMutationResult<ImportedBundleSummary>.Failure(gap);
            }
        }

        // pageId -> spaceId for every page an applied event touched, plus every space
        // touched at all - the fan-out set for the watcher notification rows below.
        var affectedPages = new Dictionary<Guid, Guid>();
        var affectedSpaceIds = new HashSet<Guid>();

        foreach (var spaceGroup in records.GroupBy(r => r.SpaceId))
        {
            // design.md §12: "a space is native or a replica... on high it materializes
            // as a replica." Spaces aren't themselves a sync event type (only their
            // pages/comments/etc. are), so importing the first event for a space this
            // instance has never seen must create the replica Space row itself - every
            // Page/Comment/etc. FK's into Spaces, and there is nothing else that would
            // ever create this row on the high side.
            await EnsureReplicaSpaceExistsAsync(spaceGroup.Key, spaceGroup.First().SpaceKey, originInstanceId, cancellationToken);
            affectedSpaceIds.Add(spaceGroup.Key);

            var gapError = await ApplySpaceEventsAsync(spaceGroup.Key, originInstanceId, spaceGroup.OrderBy(r => r.SequenceNumber).ToList(), archive, affectedPages, cancellationToken);
            if (gapError is not null)
            {
                return PageMutationResult<ImportedBundleSummary>.Failure(gapError);
            }
        }

        // design.md §8: "watching a replica is exactly how a user hears that a sync
        // bundle changed it" - added to this same change set, so the rows exist exactly
        // when the bundle's content does (one transaction, nothing on a refusal).
        AppendWatcherNotificationRows(
            await LoadWatcherTriggersAsync(affectedPages, affectedSpaceIds, cancellationToken), affectedPages);

        var currentManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        if (importState is null)
        {
            importState = new SyncImportState { OriginInstanceId = originInstanceId };
            _db.SyncImportStates.Add(importState);
        }

        importState.LastBundleNumber = manifest.BundleNumber;
        importState.LastManifestHash = currentManifestHash;
        importState.LastImportAtUtc = DateTime.UtcNow;

        _db.AuditContext = auditContext;
        _db.RaiseDomainEvent(new SyncImportedEvent(originInstanceId, manifest.BundleNumber, BuildSpaceRanges(manifest), WasDuplicate: false));

        await _db.SaveChangesAsync(cancellationToken);
        return PageMutationResult<ImportedBundleSummary>.Success(new ImportedBundleSummary(manifest.BundleNumber, records.Count, WasDuplicate: false));
    }

    private async Task EnsureReplicaSpaceExistsAsync(Guid spaceId, string spaceKey, string originInstanceId, CancellationToken cancellationToken)
    {
        var existing = FindLocal<Space>(s => s.Id == spaceId)
            ?? await _db.Spaces.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == spaceId, cancellationToken);
        if (existing is not null)
        {
            return;
        }

        // A minimal replica shell: OriginInstanceId != this instance's own id is what
        // Space.IsReplicaOf checks, so as long as this differs from whatever the
        // caller's own InstanceId is configured to, canEdit is unconditionally false
        // for it (design.md §6.4/§12) the moment any authorization check runs. Name is
        // a placeholder - space metadata itself isn't a sync event type (§12's table:
        // only pages/comments/attachments/restrictions/labels travel), so there's
        // nothing else to name it from yet.
        _db.Spaces.Add(new Space
        {
            Id = spaceId,
            Key = spaceKey,
            Name = spaceKey,
            OriginInstanceId = originInstanceId,
            IsExported = false,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.Empty,
        });
    }

    /// <summary>
    /// The per-space sequence check, run as a PRE-PASS over every space in the bundle
    /// before anything is applied — see the call site for why the position matters more
    /// than the logic. Pure: no database writes, no storage, no change-tracker entries.
    /// Baseline lines (SequenceNumber == 0) are always applied and never gap-checked, so
    /// they are skipped here exactly as they are during apply.
    /// </summary>
    private async Task<SpaceSequenceGapError?> FindSequenceGapAsync(
        Guid spaceId, string originInstanceId, List<NdjsonEventRecord> records, CancellationToken cancellationToken)
    {
        var spaceState = await _db.SyncSpaceStates.AsNoTracking().FirstOrDefaultAsync(
            s => s.OriginInstanceId == originInstanceId && s.SpaceId == spaceId, cancellationToken);

        var appliedSequence = spaceState?.AppliedSequence ?? 0;
        foreach (var record in records.Where(r => r.SequenceNumber > 0))
        {
            if (record.SequenceNumber != appliedSequence + 1)
            {
                return new SpaceSequenceGapError(spaceId, appliedSequence + 1, record.SequenceNumber);
            }

            appliedSequence = record.SequenceNumber;
        }

        return null;
    }

    /// <summary>
    /// data-model.md: SyncSpaceState.AppliedSequence is the finer-grained, per-space
    /// high-water mark inside the coarser per-bundle check above. Baseline lines
    /// (SequenceNumber == 0) are always applied and never advance or gap-check this.
    ///
    /// <para>The gap check here is now a backstop: <see cref="FindSequenceGapAsync"/> has
    /// already run over every space, so reaching a gap at this point would mean the two
    /// disagreed. It is kept rather than deleted because the alternative is a method that
    /// silently applies whatever it is handed.</para>
    /// </summary>
    private async Task<SpaceSequenceGapError?> ApplySpaceEventsAsync(
        Guid spaceId, string originInstanceId, List<NdjsonEventRecord> records, ZipArchive archive,
        Dictionary<Guid, Guid> affectedPages, CancellationToken cancellationToken)
    {
        var spaceState = await _db.SyncSpaceStates.FirstOrDefaultAsync(
            s => s.OriginInstanceId == originInstanceId && s.SpaceId == spaceId, cancellationToken);

        var appliedSequence = spaceState?.AppliedSequence ?? 0;

        foreach (var record in records)
        {
            if (record.SequenceNumber > 0)
            {
                if (record.SequenceNumber != appliedSequence + 1)
                {
                    return new SpaceSequenceGapError(spaceId, appliedSequence + 1, record.SequenceNumber);
                }

                appliedSequence = record.SequenceNumber;
            }

            await ApplyEventAsync(record, archive, cancellationToken);
            CollectAffectedPageIds(record, spaceId, affectedPages);
        }

        if (spaceState is null)
        {
            spaceState = new SyncSpaceState { OriginInstanceId = originInstanceId, SpaceId = spaceId, AppliedSequence = appliedSequence };
            _db.SyncSpaceStates.Add(spaceState);
        }
        else
        {
            spaceState.AppliedSequence = appliedSequence;
        }

        return null;
    }

    private async Task ApplyEventAsync(NdjsonEventRecord record, ZipArchive archive, CancellationToken cancellationToken)
    {
        var eventType = ParseEventType(record.EventType);
        using var payload = JsonDocument.Parse(record.PayloadJson);
        var root = payload.RootElement;

        switch (eventType)
        {
            case SyncEventType.PageUpsert:
                await ApplyPageUpsertAsync(root, cancellationToken);
                break;
            case SyncEventType.PageMove:
                await ApplyPageMoveAsync(root, cancellationToken);
                break;
            case SyncEventType.PageDelete:
                await ApplyPageDeleteOrRestoreAsync(root, isDeleted: true, cancellationToken);
                break;
            case SyncEventType.PageRestore:
                await ApplyPageDeleteOrRestoreAsync(root, isDeleted: false, cancellationToken);
                break;
            case SyncEventType.Comment:
                await ApplyCommentAsync(root, cancellationToken);
                break;
            case SyncEventType.Restrictions:
                await ApplyRestrictionAsync(root, cancellationToken);
                break;
            case SyncEventType.Labels:
                await ApplyLabelAsync(root, record.SpaceId, cancellationToken);
                break;
            case SyncEventType.PageProperties:
                await ApplyPagePropertyAsync(root, cancellationToken);
                break;
            case SyncEventType.PageMarking:
                await ApplyPageMarkingAsync(root.GetProperty("pageId").GetGuid(), root, cancellationToken);
                break;
            case SyncEventType.PageEntry:
                await ApplyPageEntryAsync(root, cancellationToken);
                break;
            case SyncEventType.Attachment:
                await ApplyAttachmentAsync(root, archive, cancellationToken);
                break;
        }
    }

    private async Task ApplyPageUpsertAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var pageId = payload.GetProperty("pageId").GetGuid();
        var page = await FindPageAsync(pageId, cancellationToken);
        var now = DateTime.UtcNow;

        if (page is null)
        {
            page = new Page { Id = pageId, CreatedAtUtc = now };
            _db.Pages.Add(page);
        }

        page.SpaceId = payload.GetProperty("spaceId").GetGuid();
        page.ParentPageId = GetNullableGuid(payload, "parentPageId");
        page.AncestorPath = payload.GetProperty("ancestorPath").GetString()!;
        // Assigned raw on purpose: RocketWikiDbContext canonicalizes Slug at the
        // persistence seam, which is precisely the case this import is - a bundle written
        // by an instance older than the case-insensitive-URL rule can carry a mixed-case
        // slug, and it must land in the replica's canonical form rather than as an address
        // that resolves on low and 404s on high. Same for the replica space's Key below.
        page.Slug = payload.GetProperty("slug").GetString()!;
        page.Title = payload.GetProperty("title").GetString()!;
        // Optional on the wire (bundles written before icons existed have no such
        // property) and unrecognised names read back as null, so an icon this build has
        // never heard of costs the page its decoration and nothing else.
        page.Icon = payload.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String
            ? PageIcons.FromWireName(iconElement.GetString())
            : null;
        page.SortOrder = payload.GetProperty("sortOrder").GetInt32();
        page.CurrentContent = payload.GetProperty("content").GetString()!;
        page.CurrentRevisionNumber = payload.GetProperty("revisionNumber").GetInt32();
        page.UpdatedAtUtc = now;
        page.IsDeleted = false; // an upsert always represents live content

        await ApplyPageRevisionsAsync(pageId, payload, cancellationToken);
        await ApplyPageMarkingAsync(pageId, payload, cancellationToken);
    }

    /// <summary>
    /// design.md §21: applies a page's protective marking, from a <c>marking</c> object
    /// on a PageUpsert payload or from a standalone PageMarking event's payload (the same
    /// shape, which is why one method serves both). Idempotent: re-applying the same
    /// marking overwrites with identical values, and the country set is replaced
    /// wholesale so a removed country really goes.
    ///
    /// <para><b>A page can never land on the high side unmarked.</b> Three cases, and the
    /// distinction between them is the whole point:</para>
    /// <list type="bullet">
    /// <item>The payload carries a marking — apply it.</item>
    /// <item>No marking in the payload and no local row (a format-1 bundle, or one
    /// produced before §21 shipped) — create the row at <b>TOP SECRET</b>. This is the
    /// fail-closed direction and it is deliberately loud in its consequences: content
    /// arriving from a lower instance without a declared classification is exactly the
    /// case where guessing OFFICIAL would be a cross-boundary disclosure, so it arrives
    /// visible to nobody but the highest-cleared and a high-side admin reviews and marks
    /// it down.</item>
    /// <item>No marking in the payload but a local row already exists — leave it alone. A
    /// legacy incremental bundle must not silently re-classify a page the high side
    /// already holds a marking for, in either direction.</item>
    /// </list>
    ///
    /// <para>An unparseable level is treated as absent, not as OFFICIAL — the same
    /// fail-closed reading a malformed rule gets (§6.3).</para>
    /// </summary>
    private async Task ApplyPageMarkingAsync(Guid pageId, JsonElement payload, CancellationToken cancellationToken)
    {
        var declared = ParseMarking(payload);

        var marking = FindLocal<PageMarking>(m => m.PageId == pageId)
            ?? await _db.PageMarkings.Include(m => m.Countries).FirstOrDefaultAsync(m => m.PageId == pageId, cancellationToken);

        if (declared is null && marking is not null)
        {
            return; // legacy payload, already-marked page - never re-classify from silence
        }

        var applied = declared ?? ProtectiveMarking.FailClosed;

        if (marking is null)
        {
            marking = new PageMarking { PageId = pageId };
            _db.PageMarkings.Add(marking);
        }

        marking.Level = applied.Level;
        marking.Prefix = applied.Prefix; // travels with the marking so a replica renders the same string
        marking.SetAtUtc = DateTime.UtcNow;
        marking.SetByUserId = null; // applied by sync, no local actor

        var existingCountries = FindLocalAll<PageMarkingCountry>(c => c.PageId == pageId)
            .Concat(marking.Countries)
            .Distinct()
            .ToList();
        foreach (var stale in existingCountries.Where(c => !applied.EyesOnly.Contains(c.CountryValue, StringComparer.Ordinal)))
        {
            marking.Countries.Remove(stale);
            _db.PageMarkingCountries.Remove(stale);
        }

        var held = marking.Countries.Select(c => c.CountryValue).ToHashSet(StringComparer.Ordinal);
        foreach (var country in applied.EyesOnly.Where(c => !held.Contains(c)))
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = pageId, CountryValue = country });
        }
    }

    /// <summary>The <c>marking</c> object as exported by BundleExportService, or null when
    /// the payload carries none (or carries one this instance cannot make sense of).</summary>
    private static ProtectiveMarking? ParseMarking(JsonElement payload)
    {
        var element = payload;
        if (payload.TryGetProperty("marking", out var nested))
        {
            if (nested.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            element = nested;
        }

        if (!element.TryGetProperty("level", out var levelElement) || levelElement.ValueKind != JsonValueKind.String
            || !ClearanceGate.TryParseLevel(levelElement.GetString(), out var level))
        {
            return null;
        }

        var countries = new List<string>();
        if (element.TryGetProperty("eyesOnly", out var eyesOnly) && eyesOnly.ValueKind == JsonValueKind.Array)
        {
            foreach (var country in eyesOnly.EnumerateArray())
            {
                if (country.ValueKind == JsonValueKind.String && country.GetString() is { } value)
                {
                    countries.Add(value);
                }
            }
        }

        // An ABSENT prefix key means null - no prefix - not "use this instance's default".
        // A bundle from an era before prefixes existed carried no national qualifier, and
        // inventing UK for it would assert something its origin never said. Only the
        // level gets a fail-closed substitution here, because only the level gates
        // anything (design.md §21.12).
        var prefix = element.TryGetProperty("prefix", out var prefixElement) && prefixElement.ValueKind == JsonValueKind.String
            ? prefixElement.GetString()
            : null;

        return ProtectiveMarking.Create(level, countries, prefix);
    }

    /// <summary>
    /// design.md §12: "Pages + full revision history ... travel with content". A format-2
    /// PageUpsert carries a <c>revisions</c> array - the page's whole history on a
    /// baseline line, the one new revision on an incremental line - and each entry is
    /// materialized as a local PageRevision row so history and bylines render on the
    /// replica. Authors arrive as shadow users, exactly like comment authors and
    /// attachment uploaders (each entry carries the same generic authorDisplayName/
    /// authorEmail keys, so EnsureShadowUserAsync is reused unchanged). An ABSENT
    /// <c>revisions</c> key is a format-1 payload (a legacy bundle already on disk, or
    /// an enrichment miss): accepted as current-state-only, exactly what format 1 always
    /// meant - never an error.
    ///
    /// Revisions are immutable (data-model.md), so an entry whose (pageId,
    /// revisionNumber) already exists locally is skipped, never rewritten - which is
    /// also what makes the baseline/incremental overlap harmless: a revision journaled
    /// to the outbox before the baseline was cut appears in both bundles and lands once.
    /// The low-side CreatedAtUtc is preserved so the history timeline stays honest.
    /// </summary>
    private async Task ApplyPageRevisionsAsync(Guid pageId, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("revisions", out var revisionsElement) || revisionsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var revisionElement in revisionsElement.EnumerateArray())
        {
            var revisionNumber = revisionElement.GetProperty("revisionNumber").GetInt32();
            var existing = FindLocal<PageRevision>(r => r.PageId == pageId && r.RevisionNumber == revisionNumber)
                ?? await _db.PageRevisions.FirstOrDefaultAsync(
                    r => r.PageId == pageId && r.RevisionNumber == revisionNumber, cancellationToken);
            if (existing is not null)
            {
                continue; // immutable - history is never rewritten, and duplicates land once
            }

            // PageRevision.AuthorUserId is a required FK into Users, same situation as
            // Attachment.UploadedByUserId - the author must exist as a shadow user first.
            var authorUserId = revisionElement.GetProperty("authorUserId").GetGuid();
            await EnsureShadowUserAsync(authorUserId, revisionElement, cancellationToken);

            _db.PageRevisions.Add(new PageRevision
            {
                PageId = pageId,
                RevisionNumber = revisionNumber,
                Title = revisionElement.GetProperty("title").GetString()!,
                Content = revisionElement.GetProperty("content").GetString()!,
                EditSummary = revisionElement.TryGetProperty("editSummary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String
                    ? summaryElement.GetString()
                    : null,
                AuthorUserId = authorUserId,
                CreatedAtUtc = revisionElement.GetProperty("createdAtUtc").GetDateTime(),
            });
        }
    }

    private async Task ApplyPageMoveAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var pageId = payload.GetProperty("pageId").GetGuid();
        var page = await FindPageAsync(pageId, cancellationToken);
        if (page is null)
        {
            return; // defensive no-op: the page hasn't arrived via an earlier PageUpsert somehow
        }

        page.ParentPageId = GetNullableGuid(payload, "newParentPageId");
        page.AncestorPath = payload.GetProperty("newAncestorPath").GetString()!;
        page.SortOrder = payload.GetProperty("newSortOrder").GetInt32();
        page.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task ApplyPageDeleteOrRestoreAsync(JsonElement payload, bool isDeleted, CancellationToken cancellationToken)
    {
        var pageIds = payload.GetProperty("pageIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
        var pages = await FindPagesAsync(pageIds, cancellationToken);
        var now = DateTime.UtcNow;
        var batchId = isDeleted ? Guid.NewGuid() : (Guid?)null;

        foreach (var page in pages)
        {
            page.IsDeleted = isDeleted;
            page.DeletedAtUtc = isDeleted ? now : null;
            page.DeleteBatchId = batchId;
            // DeletedByUserId stays null: no local user corresponds to the low-side actor.
        }
    }

    private async Task ApplyCommentAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var commentId = payload.GetProperty("commentId").GetGuid();
        var authorUserId = GetNullableGuid(payload, "authorUserId");
        if (authorUserId is not null)
        {
            await EnsureShadowUserAsync(authorUserId.Value, payload, cancellationToken);
        }

        var comment = FindLocal<Comment>(c => c.Id == commentId)
            ?? await _db.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
        {
            comment = new Comment { Id = commentId, CreatedAtUtc = DateTime.UtcNow };
            _db.Comments.Add(comment);
        }

        comment.PageId = payload.GetProperty("pageId").GetGuid();
        comment.ParentCommentId = GetNullableGuid(payload, "parentCommentId");
        comment.Body = payload.GetProperty("body").GetString() ?? string.Empty;
        comment.AuthorUserId = authorUserId ?? comment.AuthorUserId;
        comment.IsDeleted = payload.TryGetProperty("isDeleted", out var deletedEl) && deletedEl.GetBoolean();
    }

    /// <summary>design.md §12: "Authors arrive as shadow users (name/email from the event, flagged external, never loginable)". Reuses the same User.Id across bundles so repeated authorship by the same low-side user maps to one shadow row, not a fresh one each time.</summary>
    private async Task EnsureShadowUserAsync(Guid userId, JsonElement payload, CancellationToken cancellationToken)
    {
        var displayName = payload.TryGetProperty("authorDisplayName", out var nameEl) ? nameEl.GetString() : null;
        var email = payload.TryGetProperty("authorEmail", out var emailEl) ? emailEl.GetString() : null;

        var existing = FindLocal<User>(u => u.Id == userId)
            ?? await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (existing is null)
        {
            _db.Users.Add(new User
            {
                Id = userId,
                Subject = null, // never loginable
                DisplayName = displayName ?? "Unknown Author",
                Email = email,
                IsExternal = true,
                AttributesJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            });
        }
        else if (existing.IsExternal && displayName is not null)
        {
            existing.DisplayName = displayName;
            existing.Email = email;
        }
    }

    private async Task ApplyRestrictionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var ruleId = payload.GetProperty("accessRuleId").GetGuid();
        var afterElement = payload.GetProperty("after");
        var existing = FindLocal<AccessRule>(r => r.Id == ruleId)
            ?? await _db.AccessRules.FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken);

        if (afterElement.ValueKind == JsonValueKind.Null)
        {
            if (existing is not null)
            {
                _db.AccessRules.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            existing = new AccessRule { Id = ruleId, Kind = AccessRuleKind.PageRestriction, CreatedAtUtc = DateTime.UtcNow };
            _db.AccessRules.Add(existing);
        }

        // AccessRuleSnapshot's enum properties serialize as their numeric tinyint
        // values (System.Text.Json's default for enums; no string converter is
        // configured anywhere in this pipeline - see AccessRuleAuditJson), so they're
        // read back the same way here, not as names.
        existing.PageId = afterElement.GetProperty("pageId").GetGuid();
        existing.Action = (PageAction)afterElement.GetProperty("action").GetInt32();
        existing.ExpressionJson = afterElement.GetProperty("expressionJson").GetString()!;
        existing.UpdatedAtUtc = DateTime.UtcNow;
    }

    private async Task ApplyLabelAsync(JsonElement payload, Guid spaceId, CancellationToken cancellationToken)
    {
        var pageId = payload.GetProperty("pageId").GetGuid();
        var labelName = payload.GetProperty("labelName").GetString()!;
        var action = payload.GetProperty("action").GetString();

        // Labels are matched by NAME within the target space, not by Id - Label.Id was
        // never claimed to survive the crossing (unlike Page/Attachment), so the import
        // side just finds-or-creates a Label row with this name in this space.
        var label = FindLocal<Label>(l => l.SpaceId == spaceId && l.Name == labelName)
            ?? await _db.Labels.FirstOrDefaultAsync(l => l.SpaceId == spaceId && l.Name == labelName, cancellationToken);
        if (label is null)
        {
            label = new Label { SpaceId = spaceId, Name = labelName };
            _db.Labels.Add(label);
        }

        var pageLabel = FindLocal<PageLabel>(pl => pl.PageId == pageId && pl.LabelId == label.Id)
            ?? await _db.PageLabels.FirstOrDefaultAsync(pl => pl.PageId == pageId && pl.LabelId == label.Id, cancellationToken);

        if (action == "attach" && pageLabel is null)
        {
            _db.PageLabels.Add(new PageLabel { PageId = pageId, LabelId = label.Id });
        }
        else if (action == "detach" && pageLabel is not null)
        {
            _db.PageLabels.Remove(pageLabel);
        }
    }

    /// <summary>
    /// design.md §20. The payload names the registry key, never this instance's key id:
    /// the property-key registry is instance-local, so the receiving instance may have
    /// never seen this key and would otherwise be storing a value that references
    /// nothing. The key is found-or-created by its NORMALIZED name (PagePropertyKey.Normalize,
    /// the same comparison the local registry enforces uniqueness with), exactly how
    /// ApplyLabelAsync matches labels by name.
    ///
    /// Idempotent both directions: a repeated "set" overwrites with the same value, a
    /// repeated "remove" finds nothing to remove and does nothing.
    /// </summary>
    /// <summary>
    /// docs/ENTRIES-AND-FORMS-PLAN.md: an entry arriving from a lower instance.
    /// Idempotent — re-applying the same payload overwrites with identical values.
    ///
    /// <para><b>A required field that is missing is a refusal, not a default.</b> The
    /// writer always emits every field, so an absent one means a payload this build does
    /// not understand, and guessing would be how an entry silently loses its data or its
    /// marking. That is the shape of the bug page icons shipped with: the import assigned
    /// unconditionally while its neighbours handled absence deliberately, so every
    /// incremental edit stripped an icon the replica already held.</para>
    ///
    /// <para><b>The marking fails closed.</b> An unparseable or absent level lands the
    /// entry at TOP SECRET rather than OFFICIAL, exactly as
    /// <see cref="ApplyPageMarkingAsync"/> does for a page: content arriving from a lower
    /// instance without a declared classification is precisely the case where guessing
    /// the bottom of the ladder would be a cross-boundary disclosure.</para>
    /// </summary>
    private async Task ApplyPageEntryAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var entryId = payload.GetProperty("entryId").GetGuid();
        var pageId = payload.GetProperty("pageId").GetGuid();

        var entry = FindLocal<PageEntry>(e => e.Id == entryId)
            ?? await _db.PageEntries.IgnoreQueryFilters()
                .Include(e => e.Countries)
                .FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            entry = new PageEntry { Id = entryId, CreatedAtUtc = DateTime.UtcNow };
            _db.PageEntries.Add(entry);
        }

        entry.PageId = pageId;
        entry.Collection = payload.GetProperty("collection").GetString()!;
        entry.Data = payload.GetProperty("data").GetString()!;
        entry.Version = payload.GetProperty("version").GetInt32();
        entry.IsDeleted = payload.GetProperty("isDeleted").GetBoolean();
        entry.UpdatedAtUtc = DateTime.UtcNow;
        // No local actor: the payload carries none, and a replica is read-only to users
        // anyway. Same shape as every other sync-applied row.
        entry.UpdatedByUserId = null;

        var declared = ParseMarking(payload);
        var applied = declared ?? ProtectiveMarking.FailClosed;
        entry.Level = applied.Level;
        entry.Prefix = applied.Prefix;

        // The country set is replaced wholesale, so a caveat removed on the low side
        // really goes rather than accumulating forever.
        var existing = FindLocalAll<PageEntryCountry>(c => c.PageEntryId == entryId)
            .Concat(entry.Countries)
            .Distinct()
            .ToList();
        foreach (var stale in existing.Where(c => !applied.EyesOnly.Contains(c.CountryValue, StringComparer.Ordinal)))
        {
            entry.Countries.Remove(stale);
            _db.PageEntryCountries.Remove(stale);
        }

        var held = entry.Countries.Select(c => c.CountryValue).ToHashSet(StringComparer.Ordinal);
        foreach (var country in applied.EyesOnly.Where(c => !held.Contains(c)))
        {
            entry.Countries.Add(new PageEntryCountry { PageEntryId = entryId, CountryValue = country });
        }
    }

    private async Task ApplyPagePropertyAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var pageId = payload.GetProperty("pageId").GetGuid();
        var keyName = payload.GetProperty("key").GetString()!;
        var action = payload.GetProperty("action").GetString();
        var normalized = PagePropertyKey.Normalize(keyName);

        var key = FindLocal<PagePropertyKey>(k => k.KeyNormalized == normalized)
            ?? await _db.PagePropertyKeys.FirstOrDefaultAsync(k => k.KeyNormalized == normalized, cancellationToken);
        if (key is null)
        {
            key = new PagePropertyKey
            {
                Key = keyName,
                KeyNormalized = normalized,
                CreatedAtUtc = DateTime.UtcNow,
                // No local actor and no local display order: the payload deliberately
                // carries neither (§20). CreatedByUserId stays null - the same
                // "system action" shape AuditEvent.UserId has - and SortOrder lands at 0
                // for a high-side admin to arrange, since presentation order is each
                // instance's own choice, not synced content.
                CreatedByUserId = null,
                SortOrder = 0,
            };
            _db.PagePropertyKeys.Add(key);
        }

        var property = FindLocal<PageProperty>(p => p.PageId == pageId && p.PagePropertyKeyId == key.Id)
            ?? await _db.PageProperties.FirstOrDefaultAsync(
                p => p.PageId == pageId && p.PagePropertyKeyId == key.Id, cancellationToken);

        if (action == "set")
        {
            var value = payload.GetProperty("value").GetString()!;
            if (property is null)
            {
                property = new PageProperty { PageId = pageId, PagePropertyKeyId = key.Id };
                _db.PageProperties.Add(property);
            }

            property.Value = value;
            property.UpdatedAtUtc = DateTime.UtcNow;
            property.UpdatedByUserId = null; // applied by sync, no local actor
        }
        else if (action == "remove" && property is not null)
        {
            _db.PageProperties.Remove(property);
        }
    }

    private async Task ApplyAttachmentAsync(JsonElement payload, ZipArchive archive, CancellationToken cancellationToken)
    {
        var attachmentId = payload.GetProperty("attachmentId").GetGuid();
        var contentHashHex = payload.GetProperty("contentHash").GetString()!;
        var isDeleted = payload.TryGetProperty("isDeleted", out var deletedEl) && deletedEl.GetBoolean();

        // Validated before it is used to name a blobs/ entry OR written to a binary(32)
        // column. Convert.FromHexString below threw a bare FormatException on a non-hex
        // string - a refusal in substance that escaped the CLI's filter as a crash - and a
        // well-formed-but-short hex string would have got past it only to fail at
        // SaveChanges as a truncation error, after the blob was already in storage.
        if (!IsSha256Hex(contentHashHex))
        {
            throw new InvalidDataException(
                $"Attachment {attachmentId} declares a contentHash that is not a SHA-256 hex digest.");
        }

        // Attachments.UploadedByUserId is a required FK into Users - Guid.Empty would
        // violate it on the high side, since there's obviously no local user with that
        // id. The uploader arrives as a shadow user, exactly like a comment author.
        var uploaderUserId = payload.GetProperty("uploadedByUserId").GetGuid();
        await EnsureShadowUserAsync(uploaderUserId, payload, cancellationToken);

        var attachment = FindLocal<Attachment>(a => a.Id == attachmentId)
            ?? await _db.Attachments.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        var now = DateTime.UtcNow;

        if (attachment is null)
        {
            // A fresh StorageKey local to THIS instance's own storage backend - the
            // low side's key is opaque and meaningless here (deliberately excluded
            // from the sync payload in the first place - see SyncOutboxWriter).
            var storageKey = StorageKeys.ForAttachment(now);

            // These bytes have already been re-hashed and matched against this very entry
            // name by VerifyBlobIntegrityAsync, before anything in this bundle was
            // applied - so what lands in storage is provably the content the (hash-chained)
            // event line declares. The bound is repeated anyway because a stream copy that
            // trusts an earlier pass is a stream copy with no bound of its own.
            var blobEntry = archive.GetEntry($"blobs/{contentHashHex}");
            if (blobEntry is not null)
            {
                await using var entryStream = blobEntry.Open();
                await using var bounded = new BoundedReadStream(entryStream, _limits.MaxBlobBytes, blobEntry.FullName);
                await _fileStorage.SaveAsync(storageKey, bounded, payload.GetProperty("contentType").GetString()!, cancellationToken);
            }
            // A missing blob entry is not an error here - export skips an attachment whose
            // object is absent from its own storage (design.md §10: an operational fault,
            // not something export should crash on), so refusing the whole bundle would
            // turn one origin-side fault into a boundary the operator cannot cross without
            // a re-export. ExistsAsync at download time is what surfaces it. Note the
            // residual this leaves: stripping a blobs/ entry from a bundle omits that
            // attachment's content rather than substituting it, and the omission is
            // visible at the first download attempt.

            attachment = new Attachment
            {
                Id = attachmentId,
                StorageKey = storageKey,
                UploadedByUserId = uploaderUserId, // resolved to a shadow user above
                CreatedAtUtc = now,
            };
            _db.Attachments.Add(attachment);
        }

        attachment.PageId = payload.GetProperty("pageId").GetGuid();
        attachment.FileName = payload.GetProperty("fileName").GetString()!;
        attachment.ContentType = payload.GetProperty("contentType").GetString()!;
        attachment.SizeBytes = payload.GetProperty("sizeBytes").GetInt64();
        attachment.ContentHash = Convert.FromHexString(contentHashHex);
        attachment.IsDeleted = isDeleted;
        attachment.DeletedAtUtc = isDeleted ? now : null;
    }

    /// <summary>Which pages a just-applied event touched, for the watcher fan-out. A
    /// restriction REMOVAL (null <c>after</c>) is deliberately not collected as a page:
    /// its payload carries no page id, and a permission widening isn't "content you
    /// watch changed" - the space-level trigger still covers the bundle.</summary>
    private static void CollectAffectedPageIds(NdjsonEventRecord record, Guid spaceId, Dictionary<Guid, Guid> affectedPages)
    {
        using var payload = JsonDocument.Parse(record.PayloadJson);
        var root = payload.RootElement;

        switch (ParseEventType(record.EventType))
        {
            case SyncEventType.PageUpsert:
            case SyncEventType.PageMove:
            case SyncEventType.Comment:
            case SyncEventType.Labels:
            case SyncEventType.PageProperties:
            // A marking change is a change to the page, so watchers hear about it and the
            // page is reindexed - the same treatment a label or property change gets.
            // Note this is a NOTIFICATION decision, not an access one: the recipients are
            // still canView-filtered downstream, so a page that just became invisible to
            // a watcher does not ping them about it.
            case SyncEventType.PageMarking:
            case SyncEventType.Attachment:
                affectedPages[root.GetProperty("pageId").GetGuid()] = spaceId;
                break;
            case SyncEventType.PageDelete:
            case SyncEventType.PageRestore:
                foreach (var pageIdElement in root.GetProperty("pageIds").EnumerateArray())
                {
                    affectedPages[pageIdElement.GetGuid()] = spaceId;
                }

                break;
            case SyncEventType.Restrictions:
                if (root.GetProperty("after").ValueKind != JsonValueKind.Null)
                {
                    affectedPages[root.GetProperty("after").GetProperty("pageId").GetGuid()] = spaceId;
                }

                break;
        }
    }

    private sealed record WatcherTrigger(Guid UserId, Guid? PageId, Guid? SpaceId);

    private async Task<List<WatcherTrigger>> LoadWatcherTriggersAsync(
        Dictionary<Guid, Guid> affectedPages, HashSet<Guid> affectedSpaceIds, CancellationToken cancellationToken)
    {
        if (affectedPages.Count == 0 && affectedSpaceIds.Count == 0)
        {
            return [];
        }

        var affectedPageIds = affectedPages.Keys.ToArray();
        var spaceIds = affectedSpaceIds.ToArray();
        return (await _db.Watches.AsNoTracking()
                .Where(w => (w.PageId != null && affectedPageIds.Contains(w.PageId.Value)) ||
                    (w.SpaceId != null && spaceIds.Contains(w.SpaceId.Value)))
                .Select(w => new { w.UserId, w.PageId, w.SpaceId })
                .ToListAsync(cancellationToken))
            .Select(w => new WatcherTrigger(w.UserId, w.PageId, w.SpaceId))
            .ToList();
    }

    /// <summary>
    /// design.md §8: a Watch on a replica page/space is "exactly how a user hears that a
    /// sync bundle changed it". One Notification row per watcher per BUNDLE - never per
    /// event - so a 500-event bundle is one nudge, not 500. Rows only: this runs in the
    /// RocketWiki.Sync CLI, where there is no hub connection and no live token for any
    /// recipient, so nothing is (or could be) pushed - the rows surface on the
    /// recipient's next `notifications` fetch.
    ///
    /// The canView discipline differs from NotificationDispatcher's by necessity, not
    /// preference: canView requires a Principal built from a validated token (design.md
    /// §6.1), and in this offline CLI process no recipient has one - and the local User
    /// mirror is never a substitute (§6.1). So instead of checking at send time, the
    /// check is DEFERRED to read time: TitleSnapshot stays null (nothing attested here
    /// may disclose a title), and NotificationReadModelService suppresses the ENTIRE row
    /// - not just the title - unless the recipient's live, token-built Principal passes
    /// canView (page rows) / holds a space role (space rows) at fetch. Fail closed
    /// either way: a recipient who lost access since watching sees nothing at all.
    ///
    /// Row shape: page-scoped (PageId + SpaceId) when exactly one watched page was
    /// affected and no space watch fired, else space-scoped (PageId null). A bundle
    /// spanning several watched spaces still yields ONE row, naming one space
    /// deterministically (smallest id) - the notification is a nudge to go look, not a
    /// ledger of everything the bundle did. ActorUserId is null: a sync import is the
    /// system's action (rendered as "System", same as the sync.import audit row).
    /// </summary>
    private void AppendWatcherNotificationRows(List<WatcherTrigger> triggers, Dictionary<Guid, Guid> affectedPages)
    {
        var now = DateTime.UtcNow;
        foreach (var userTriggers in triggers.GroupBy(t => t.UserId))
        {
            var watchedPageIds = userTriggers.Where(t => t.PageId is not null).Select(t => t.PageId!.Value).Distinct().ToList();
            var watchedSpaceIds = userTriggers.Where(t => t.SpaceId is not null).Select(t => t.SpaceId!.Value).Distinct().ToList();

            Guid? pageId = null;
            Guid spaceId;
            if (watchedSpaceIds.Count == 0 && watchedPageIds.Count == 1)
            {
                pageId = watchedPageIds[0];
                spaceId = affectedPages[watchedPageIds[0]];
            }
            else
            {
                spaceId = watchedSpaceIds
                    .Concat(watchedPageIds.Select(p => affectedPages[p]))
                    .Distinct()
                    .Order()
                    .First();
            }

            _db.Notifications.Add(new Notification
            {
                RecipientUserId = userTriggers.Key,
                Type = NotificationType.SyncImported,
                PageId = pageId,
                SpaceId = spaceId,
                ActorUserId = null,
                TitleSnapshot = null, // no canView was (or could be) evaluated here - see doc above
                CreatedAtUtc = now,
            });
        }
    }

    /// <summary>
    /// A whole bundle applies inside ONE unsaved unit of work (design.md §12: "a rejected
    /// bundle never partially lands" - the single SaveChangesAsync at the end of
    /// ImportAsync is what makes that atomic). That means a page created earlier in this
    /// SAME bundle - e.g. a page created and then immediately moved/commented/restricted
    /// before the next incremental export - is only in the change tracker, not yet in the
    /// database. A plain `_db.Pages.FirstOrDefaultAsync(...)` always round-trips to the
    /// database and will not see it, silently no-op-ing (or worse, re-Add()-ing a
    /// duplicate). Every lookup in this file must check the local change tracker first.
    /// </summary>
    private T? FindLocal<T>(Func<T, bool> predicate) where T : class =>
        _db.ChangeTracker.Entries<T>().Select(e => e.Entity).FirstOrDefault(predicate);

    /// <summary>Every tracked entity matching the predicate, not just the first — needed
    /// where a page owns a SET of rows in this unit of work (marking countries) rather
    /// than one.</summary>
    private List<T> FindLocalAll<T>(Func<T, bool> predicate) where T : class =>
        _db.ChangeTracker.Entries<T>()
            .Where(e => e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .Where(predicate)
            .ToList();

    private async Task<Page?> FindPageAsync(Guid pageId, CancellationToken cancellationToken) =>
        FindLocal<Page>(p => p.Id == pageId)
        ?? await _db.Pages.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);

    private async Task<List<Page>> FindPagesAsync(List<Guid> pageIds, CancellationToken cancellationToken)
    {
        var found = new List<Page>();
        var remaining = new List<Guid>();
        foreach (var pageId in pageIds)
        {
            var local = FindLocal<Page>(p => p.Id == pageId);
            if (local is not null)
            {
                found.Add(local);
            }
            else
            {
                remaining.Add(pageId);
            }
        }

        if (remaining.Count > 0)
        {
            found.AddRange(await _db.Pages.IgnoreQueryFilters().Where(p => remaining.Contains(p.Id)).ToListAsync(cancellationToken));
        }

        return found;
    }

    private static IReadOnlyList<SyncImportedSpaceRange> BuildSpaceRanges(BundleManifest manifest) =>
        manifest.SpaceEventRanges
            .Select(kv => new SyncImportedSpaceRange(kv.Key, kv.Value.FromSequence, kv.Value.ToSequence, kv.Value.EventCount))
            .ToList();

    private static Guid? GetNullableGuid(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var element) && element.ValueKind != JsonValueKind.Null ? element.GetGuid() : null;

    private static List<NdjsonEventRecord> ParseEvents(byte[] eventsBytes, int maxLines)
    {
        var text = Encoding.UTF8.GetString(eventsBytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > maxLines)
        {
            // Each line becomes an object held for the whole import, so the count needs a
            // ceiling of its own rather than one inferred from the byte cap and the
            // shortest legal line.
            throw new BundleLimitExceededException(
                $"the events file has {lines.Length} lines, above the {maxLines}-line ceiling.");
        }

        // Not `Deserialize(...)!`: a line that is literally `null` deserializes to null, and
        // the null-forgiving operator turned that into a NullReferenceException three
        // frames away from the malformed line. A named refusal says which line, and it is
        // the kind of exception the CLI's refusal filter is meant to turn into an audit
        // row rather than a stack trace.
        var records = new List<NdjsonEventRecord>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            records.Add(JsonSerializer.Deserialize<NdjsonEventRecord>(lines[i], JsonOptions)
                ?? throw new InvalidDataException($"Line {i + 1} of the bundle's events file is not an event record."));
        }

        return records;
    }

    /// <summary>
    /// The event type named on an NDJSON line. <c>Enum.Parse</c> threw a bare
    /// <see cref="ArgumentException"/> for an unrecognised name — a refusal in substance
    /// but not in shape, so it escaped the CLI's filter and crashed instead of leaving the
    /// <c>sync.import.refused</c> row. <c>Enum.IsDefined</c> is the second half:
    /// <c>TryParse</c> happily accepts <c>"57"</c> as a SyncEventType.
    ///
    /// <para>Refusing rather than skipping is deliberate. A name this build does not know
    /// means either corruption or a format this instance predates, and the second case is
    /// already handled loudly upstream by the format-version check — so silently dropping
    /// the event would be absorbing an unknown, which is the one thing §12 never does.</para>
    /// </summary>
    private static SyncEventType ParseEventType(string eventTypeName) =>
        Enum.TryParse<SyncEventType>(eventTypeName, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"The bundle carries an event of type '{eventTypeName}', which this instance does not recognise.");

    // --- Integrity and bounds on the archive itself (design.md §12) ---------------------

    /// <summary>
    /// Thrown from inside a bounded read; converted to a <see cref="BundleTooLargeError"/>
    /// at the single catch in <see cref="ImportCoreAsync"/>. Private because it is a
    /// control-flow detail of this class and must never be part of anyone's contract - a
    /// caller sees the typed refusal, like every other integrity failure.
    /// </summary>
    private sealed class BundleLimitExceededException(string reason)
        : Exception($"Bundle exceeds the import ceiling: {reason}");

    /// <summary>
    /// Entry count and declared total expansion, from the central directory alone - no
    /// decompression. <see cref="ZipArchiveEntry.Length"/> is what the archive CLAIMS an
    /// entry expands to, so this cannot be the only bound (the bounded copies below are
    /// what make a lie useless); it is here because refusing an obvious bomb should not
    /// require reading it first.
    /// </summary>
    private void CheckArchiveShape(ZipArchive archive)
    {
        if (archive.Entries.Count > _limits.MaxEntryCount)
        {
            throw new BundleLimitExceededException(
                $"it declares {archive.Entries.Count} entries, above the {_limits.MaxEntryCount}-entry ceiling.");
        }

        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            declaredTotal += entry.Length;
            if (declaredTotal > _limits.MaxTotalUncompressedBytes)
            {
                throw new BundleLimitExceededException(
                    $"it declares more than {_limits.MaxTotalUncompressedBytes} bytes of uncompressed content.");
            }
        }
    }

    /// <summary>
    /// design.md §12: <b>every attachment's bytes are re-hashed before any of them is
    /// written.</b> An entry under <c>blobs/</c> is named for the SHA-256 of its own
    /// content (BundleExportService.WriteBlobsAsync), and the event line that references
    /// it carries the same hex string — and THAT line is inside the bytes
    /// <c>manifest.PayloadSha256</c> covers, which the manifest chain covers in turn. So
    /// recomputing the hash and requiring it to equal the entry's name is what extends the
    /// existing chain over attachment content: it needs no change to the bundle format,
    /// because the binding was already there and simply never checked.
    ///
    /// <para>Without it, an attachment's file content could be substituted anywhere on the
    /// transfer medium with nothing detecting it — on the one boundary whose entire
    /// purpose is that only vetted content crosses. The declared hash was written straight
    /// into <c>Attachment.ContentHash</c> and the bytes streamed straight to storage, so
    /// the high side then held a row asserting a hash its own blob did not have.</para>
    ///
    /// <para><b>Position matters as much as the check.</b> This is a PRE-PASS, for the
    /// same reason the per-space sequence check is one: attachments are written to storage
    /// as their events are applied, and a refusal partway through leaves those blobs
    /// behind forever (SaveChangesAsync is never reached, so the ROWS evaporate and the
    /// bytes do not, and a corrected re-import mints a fresh storage key). Verified here,
    /// nothing has been written yet.</para>
    ///
    /// <para>Every <c>blobs/</c> entry is verified, not only the ones some event line
    /// happens to reference — an unreferenced entry is not a thing this exporter produces,
    /// so its presence is already a reason to look, and checking all of them means no
    /// entry can be smuggled in behind a reference that only appears in a later bundle.
    /// The entry name itself must be a well-formed SHA-256 (64 hex characters): that is
    /// the invariant the whole scheme rests on, so it is checked rather than assumed.</para>
    /// </summary>
    private async Task<BundleBlobTamperedError?> VerifyBlobIntegrityAsync(
        ZipArchive archive, CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("blobs/", StringComparison.Ordinal))
            {
                continue;
            }

            // A bare directory entry ("blobs/") carries no content and is not a blob.
            // Our exporter never writes one; a re-zip by an operator tool might.
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var declaredHashHex = entry.FullName["blobs/".Length..];
            if (!IsSha256Hex(declaredHashHex))
            {
                return new BundleBlobTamperedError(
                    $"'{entry.FullName}' is not named for a SHA-256 content hash. Every blobs/ entry must be named " +
                    "for the hash of its own bytes - that naming IS the integrity binding between an attachment's " +
                    "content and the hash-covered event line that references it.");
            }

            var actualHashHex = await HashEntryAsync(entry, _limits.MaxBlobBytes, cancellationToken);
            if (!string.Equals(actualHashHex, declaredHashHex, StringComparison.OrdinalIgnoreCase))
            {
                // The hash itself is not repeated back in full: it is in the entry name,
                // which the operator can read. What matters is which entry and that the
                // bytes are not the bytes the bundle claims.
                return new BundleBlobTamperedError(
                    $"'{entry.FullName}' contains bytes that hash to {actualHashHex}, not to the content hash its " +
                    "own entry name declares - the attachment's content was substituted or corrupted in transit.");
            }
        }

        return null;
    }

    /// <summary>64 hex characters, and nothing else. Case-insensitive because
    /// <c>Convert.ToHexString</c> emits upper and a hand-built bundle may not.</summary>
    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SHA-256 of an entry's decompressed bytes, computed streaming so a blob is
    /// never held in memory, and bounded so hashing one cannot itself be the attack.</summary>
    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        await using var bounded = new BoundedReadStream(stream, maxBytes, entry.FullName);
        return Convert.ToHexString(await SHA256.HashDataAsync(bounded, cancellationToken));
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchive archive, string entryName, long maxBytes, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Bundle is missing '{entryName}'.");

        // The declared size first (free), then the copy counts for itself - see
        // CheckArchiveShape for why the declared number cannot be trusted alone.
        if (entry.Length > maxBytes)
        {
            throw new BundleLimitExceededException(
                $"'{entryName}' declares {entry.Length} uncompressed bytes, above its {maxBytes}-byte ceiling.");
        }

        await using var stream = entry.Open();
        await using var bounded = new BoundedReadStream(stream, maxBytes, entryName);
        using var memory = new MemoryStream();
        await bounded.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

}
