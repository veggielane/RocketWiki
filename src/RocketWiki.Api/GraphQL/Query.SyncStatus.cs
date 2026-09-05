using System.Security.Claims;
using System.Text.Json;
using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Features;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// design.md §12 Operations: "An admin sync status page shows the last bundle applied,
/// per-space sequence positions, and loud warnings on gaps or chain breaks." One view,
/// both roles of an instance:
/// - low side: every exported native space's outbox position and how much is pending
///   (an ever-growing pending count is the "export job isn't running" warning);
/// - high side: per origin instance, the last bundle applied, when, the manifest hash
///   the chain must continue from, and each replica space's applied sequence.
/// Gaps and chain breaks are refused at import time (BundleImportService) and reported
/// loudly by the RocketWiki.Sync CLI; what this query exposes is the durable state the
/// admin page renders and compares against ("bundle 41 applied three weeks ago" IS the
/// warning when bundle 45 just arrived).
///
/// <para><see cref="Enabled"/> is the <c>Sync</c> feature flag (docs/CONFIGURATION.md
/// "Feature flags"), the sibling of <c>assistantStatus.configured</c> and
/// <c>gitlabStatus.configured</c>: the fact the admin page needs in order to explain
/// itself rather than a second mechanism. When false, <c>setSpaceExported</c> refuses,
/// and the rest of this view still reports the durable state honestly — an exported space
/// with pending events is MORE worth showing on an instance whose sync was just switched
/// off, not less. Always present in the schema, whichever way the flag is set.</para>
/// </summary>
public sealed record SyncStatusView(
    bool Enabled,
    string LocalInstanceId,
    IReadOnlyList<ExportedSpaceSyncStatusView> ExportedSpaces,
    IReadOnlyList<SyncOriginStatusView> Origins);

/// <summary>Low side, one per exported native space (design.md §12: only a native space is ever exported).</summary>
public sealed record ExportedSpaceSyncStatusView(
    Guid SpaceId,
    string SpaceKey,
    long LastOutboxSequence,
    int PendingEventCount,
    int? LastExportedBundle);

/// <summary>High side, one per origin instance bundles have been imported from (data-model.md: SyncImportState).</summary>
public sealed record SyncOriginStatusView(
    string OriginInstanceId,
    int LastBundleNumber,
    string LastManifestHash,
    DateTime LastImportAtUtc,
    IReadOnlyList<SyncOriginSpaceStatusView> Spaces);

/// <summary>Per-replica-space applied-sequence high-water mark (data-model.md: SyncSpaceState).</summary>
public sealed record SyncOriginSpaceStatusView(Guid SpaceId, string SpaceKey, long AppliedSequence);

public partial class Query
{
    /// <summary>
    /// Admin-only, with the same explicit-error-not-absence shape as
    /// <see cref="AuditEvents"/> and for the same reason: there is no existence to
    /// leak (every instance self-evidently has sync state, even if empty), so an
    /// honest refusal beats a silently empty result — and design.md §7 requires the
    /// denial itself be recorded either way. Positions and hashes here are operational
    /// metadata, never page content.
    /// </summary>
    [AuditAction("sync.status")]
    [UseAuditDispatch]
    public async Task<SyncStatusView> SyncStatus(
        ClaimsPrincipal claimsPrincipal,
        [Service] RocketWikiDbContext db,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] InstanceIdentity instanceIdentity,
        [Service] FeatureFlagSnapshot features,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        if (!instanceRoleAccessor.IsInstanceAdmin)
        {
            // Anonymous is refused the same way but records nothing, for the reason
            // AuditEvents states at the identical gate: no acting user exists to
            // attribute the row to, and DbAuditSink refuses UserId-less rows (§7).
            if (claimsPrincipal.Identity?.IsAuthenticated == true)
            {
                // Same {"reason": ...} details shape as every other denial row (§7), so
                // the audit log viewer reads one format for all denials (ReadDenialAudit).
                await auditSink.RecordAsync(
                    new AuditRecord(
                        "sync.status", AuditOutcome.Denied,
                        DetailsJson: JsonSerializer.Serialize(new { reason = "instance admin required" })),
                    cancellationToken);
            }

            throw new GraphQLException("Instance admin required to view sync status.");
        }

        // IgnoreQueryFilters throughout: an archived/soft-deleted space still has sync
        // state, and hiding it here would make the status page silently disagree with
        // what import/export actually track. Admin-only, and only keys/positions leave.
        var exportedSpaces = await db.Spaces.IgnoreQueryFilters()
            .Where(s => s.IsExported && s.OriginInstanceId == instanceIdentity.LocalInstanceId)
            .OrderBy(s => s.Key)
            .Select(s => new
            {
                s.Id,
                s.Key,
                s.LastOutboxSequence,
                PendingEventCount = db.SyncOutboxEvents.Count(e => e.SpaceId == s.Id && e.ExportedInBundle == null),
                LastExportedBundle = db.SyncOutboxEvents
                    .Where(e => e.SpaceId == s.Id && e.ExportedInBundle != null)
                    .Max(e => e.ExportedInBundle),
            })
            .ToListAsync(cancellationToken);

        var importStates = await db.SyncImportStates
            .OrderBy(s => s.OriginInstanceId)
            .ToListAsync(cancellationToken);

        var spaceStates = await db.SyncSpaceStates.ToListAsync(cancellationToken);
        var spaceStateIds = spaceStates.Select(s => s.SpaceId).Distinct().ToList();
        var spaceKeysById = await db.Spaces.IgnoreQueryFilters()
            .Where(s => spaceStateIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Key, cancellationToken);

        var origins = importStates
            .Select(state => new SyncOriginStatusView(
                state.OriginInstanceId,
                state.LastBundleNumber,
                state.LastManifestHash,
                state.LastImportAtUtc,
                spaceStates
                    .Where(s => s.OriginInstanceId == state.OriginInstanceId)
                    .OrderBy(s => spaceKeysById.GetValueOrDefault(s.SpaceId, string.Empty))
                    .Select(s => new SyncOriginSpaceStatusView(
                        s.SpaceId,
                        spaceKeysById.GetValueOrDefault(s.SpaceId, string.Empty),
                        s.AppliedSequence))
                    .ToList()))
            .ToList();

        return new SyncStatusView(
            features.Sync,
            instanceIdentity.LocalInstanceId,
            exportedSpaces
                .Select(s => new ExportedSpaceSyncStatusView(s.Id, s.Key, s.LastOutboxSequence, s.PendingEventCount, s.LastExportedBundle))
                .ToList(),
            origins);
    }
}
