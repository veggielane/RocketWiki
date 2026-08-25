using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// EF-backed implementation of IBundleImportService. design.md §12: strictly ordered,
/// gap-refusing, idempotent, hash-chain-verified. Every check that can fail happens
/// BEFORE any entity in the bundle is applied, so a rejected bundle never partially lands.
/// </summary>
public class BundleImportService : IBundleImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RocketWikiDbContext _db;
    private readonly IFileStorage _fileStorage;

    public BundleImportService(RocketWikiDbContext db, IFileStorage fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
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

    private async Task<PageMutationResult<ImportedBundleSummary>> ImportCoreAsync(
        string bundleFilePath, string originInstanceId, AuditContext auditContext, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(bundleFilePath);

        var manifestBytes = await ReadEntryAsync(archive, "manifest.json", cancellationToken);
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

        var eventsEntryName = BundleFormat.EventsEntryName(manifest.FormatVersion);
        var eventsBytes = await ReadEntryAsync(archive, eventsEntryName, cancellationToken);
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

        var records = ParseEvents(eventsBytes);

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
    /// data-model.md: SyncSpaceState.AppliedSequence is the finer-grained, per-space
    /// high-water mark inside the coarser per-bundle check above. Baseline lines
    /// (SequenceNumber == 0) are always applied and never advance or gap-check this.
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
        var eventType = Enum.Parse<SyncEventType>(record.EventType);
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
        page.Slug = payload.GetProperty("slug").GetString()!;
        page.Title = payload.GetProperty("title").GetString()!;
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

        return ProtectiveMarking.Create(level, countries);
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
            var storageKey = $"attachments/{now:yyyy'/'MM}/{Guid.CreateVersion7()}";

            var blobEntry = archive.GetEntry($"blobs/{contentHashHex}");
            if (blobEntry is not null)
            {
                await using var entryStream = blobEntry.Open();
                await _fileStorage.SaveAsync(storageKey, entryStream, payload.GetProperty("contentType").GetString()!, cancellationToken);
            }
            // A missing blob entry (e.g. a duplicate-content attachment whose blob was
            // already packed under a different attachment's line) is not an error here -
            // ExistsAsync at download time is what surfaces a genuinely missing object.

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

        switch (Enum.Parse<SyncEventType>(record.EventType))
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

    private static List<NdjsonEventRecord> ParseEvents(byte[] eventsBytes)
    {
        var text = Encoding.UTF8.GetString(eventsBytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Select(line => JsonSerializer.Deserialize<NdjsonEventRecord>(line, JsonOptions)!).ToList();
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Bundle is missing '{entryName}'.");
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }
}
