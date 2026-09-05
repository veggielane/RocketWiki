using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.RealTime;

/// <summary>What JoinEditSession returns to an authorized joiner. Role is
/// <c>seeder</c> (you create the Y.Doc from the page's CurrentContent at
/// BaseRevisionNumber and push the resulting encoded state as your first update) or
/// <c>joiner</c> (you create an empty Y.Doc and apply UpdateLog in order — if the log
/// is still empty, the seed arrives as a live UpdateReceived). BaseRevisionNumber is
/// the <c>expectedRevisionNumber</c> for the session's next updatePageContent save.</summary>
public sealed record EditSessionJoinResult(string Role, int BaseRevisionNumber, byte[][] UpdateLog);

/// <summary>
/// CRDT co-editing over the existing hub (design.md §8, resolving §17's open bullet).
///
/// <b>The load-bearing decision — relay, not server-side CRDT.</b> The server relays
/// opaque Yjs binary updates between session members and retains a session-scoped
/// update log for late joiners; it never interprets CRDT state. The alternative —
/// materializing the document server-side so the server could author saves — needs a
/// C# Yjs implementation, and the only one (yjs/ycs) is effectively unmaintained:
/// last commit August 2023, 56 commits total, "latest tested Yjs version 13.4.14"
/// (a 2021-era protocol level), verified against github.com/yjs/ycs at the time this
/// shipped. Building export-control-grade infrastructure on that is a worse risk than
/// the relay's honest limitation, which is: <b>the server cannot validate live update
/// content</b>. Authorization is therefore membership-gated (canEdit at join, §6.7,
/// re-checked on rule changes), and the AUTHORITATIVE write path is unchanged — a
/// designated client serializes the doc to Markdown and calls updatePageContent, so
/// nothing reaches storage, sync, search, or embeddings except through the
/// already-guarded pipeline (revision + audit + outbox in one transaction, §7/§12).
/// Live updates are ephemeral traffic like presence but, unlike presence, they carry
/// content, so they are held to a stricter §15 tier: update bytes are page content in
/// CRDT encoding, and nothing derived from their CONTENT ever reaches telemetry — only
/// byte counts (see ApiTelemetry's coedit section).
///
/// <b>Replica spaces refuse co-editing</b> (§12): canEdit is unconditionally false
/// there, so the join gate below refuses with reason <c>replica-read-only</c> before
/// any session exists — content writes are blocked on replicas anyway, and a session
/// whose saves could never land would be a lie.
///
/// <b>Restart semantics, stated plainly:</b> sessions and logs are in-memory only.
/// An API restart mid-session drops them; clients reconnect, the first re-joiner
/// becomes seeder of a fresh session from the page's last SAVED content, and unsaved
/// live edits survive only in each client's local Y.Doc (from which that client can
/// save normally). Log persistence across restarts is deliberately out of scope for
/// v1 — the durable record is the save path, not the relay.
/// </summary>
public sealed partial class NotificationsHub
{
    internal static string EditGroupName(Guid pageId) => $"edit:{pageId}";

    private const string RoleSeeder = "seeder";
    private const string RoleJoiner = "joiner";

    /// <summary>
    /// Join the page's edit session, creating it when first. canEdit gates the join —
    /// the same fail-closed shape as JoinPage one tier up (§6.7): every refusal
    /// (nonexistent page, canView failure, canEdit failure, replica) is the same
    /// silent <c>null</c>, indistinguishable to the caller from nothing. The audit
    /// log gets the distinction the caller doesn't (§6.7 "indistinguishable to the
    /// caller, not to the audit log"): a denial with the failing reason in hand is
    /// recorded as a Denied row — mirroring ReadDenialAudit / the mutation-denial
    /// path — while a genuine not-found audits nothing, §7's vocabulary having no
    /// access decision to record there.
    /// </summary>
    [AuditAction(EditSessionAudit.JoinedAction)]
    public async Task<EditSessionJoinResult?> JoinEditSession(Guid pageId)
    {
        // The CoEditing feature flag (docs/CONFIGURATION.md "Feature flags"), checked
        // before any permission work: "off" is the same silent null every refusal below
        // answers, so the SPA falls back to the solo editor exactly as it does for a
        // denied join (progressive enhancement is its documented contract). Not audited
        // — no access decision was made, and §7's denied vocabulary names a principal
        // refused by a rule, which this is not (the NotFound arm makes the same call).
        // Counted, so an operator can see the feature is off from the metric rather
        // than from a silence. This is the ONE seam: PushUpdate, PushAwareness and
        // ReseedEditSession are membership-gated and a session that cannot be joined
        // has no members.
        if (!features.CoEditing)
        {
            ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoinDisabled);
            return null;
        }

        var principal = PrincipalBuilder.Build(Context.User);
        if (principal is null)
        {
            ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoinNoPrincipal);
            return null;
        }

        // Facts, not GetPageAsync: the join needs canEdit (with its denial reason for
        // the audit row), the replica invariant, and not-found-vs-denied - exactly
        // what IPagePermissionReadService computes, fail closed, through the single
        // EffectivePermissionCalculator path (§6.4/§6.7).
        var facts = await pagePermissionReadService.GetPermissionFactsAsync([pageId], principal, Context.ConnectionAborted);
        if (!facts.TryGetValue(pageId, out var fact))
        {
            ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoinNotFound);
            return null;
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Subject == principal.UserId, Context.ConnectionAborted);
        if (user is null)
        {
            // Same shape as JoinPage's no-local-user branch - and with no local User
            // row there is nobody to attribute an audit row to either (DbAuditSink's
            // fail-closed reasoning): no membership, no row, counted for the operator.
            ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoinNoLocalUser);
            return null;
        }

        // FirstOrDefault + silent null, not First. The contract above says EVERY
        // refusal is the same silent null, indistinguishable to the caller — and a
        // page deleted between the permission check and this read would instead have
        // thrown InvalidOperationException out of the hub method, which is both a
        // different observable outcome and the one shape §6.7 says a caller must not
        // be able to tell apart from the others.
        var pageInfo = await db.Pages.AsNoTracking()
            .Where(p => p.Id == pageId)
            .Select(p => new { p.CurrentRevisionNumber, SpaceKey = p.Space!.Key })
            .FirstOrDefaultAsync(Context.ConnectionAborted);
        if (pageInfo is null)
        {
            ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoinNotFound);
            return null;
        }
        var clientIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!fact.Permission.CanEdit)
        {
            // Denied with the reason in hand (restriction:{pageId}:{ruleId},
            // no-space-role, insufficient-space-role, or replica-read-only - the §12
            // refusal surfaces here as the calculator's unconditional edit denial).
            await EditSessionAudit.RecordAsync(
                db, user.Id, EditSessionAudit.JoinedAction, AuditOutcome.Denied, pageId, pageInfo.SpaceKey,
                EditSessionAudit.ReasonDetails(fact.Permission.EditDenialReason ?? "forbidden"),
                Context.ConnectionId, clientIp, Context.ConnectionAborted);
            ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoinDenied);
            return null;
        }

        var member = new EditSessionMember(Context.ConnectionId, user.Id, principal, clientIp);
        var outcome = editSessions.Join(pageId, member, pageInfo.CurrentRevisionNumber);
        try
        {
            // Audit before any group membership exists: a failed insert fails the
            // join (§7 fail-closed applied to observability), and the only thing to
            // compensate is the in-memory registry entry.
            await EditSessionAudit.RecordAsync(
                db, user.Id, EditSessionAudit.JoinedAction, AuditOutcome.Success, pageId, pageInfo.SpaceKey,
                JsonSerializer.Serialize(new
                {
                    role = outcome.IsSeeder ? RoleSeeder : RoleJoiner,
                    baseRevisionNumber = outcome.BaseRevisionNumber,
                }),
                Context.ConnectionId, clientIp, Context.ConnectionAborted);
        }
        catch
        {
            // The compensating Leave can produce a reseed demand, and dropping it
            // deadlocks the session. If this joiner was the designated seeder with an
            // empty log, LeaveLocked promotes the next member and returns a seeder_lost
            // demand — but that member was already told role: "joiner" with an empty log,
            // so it waits for an UpdateReceived nobody will ever send, and later joiners
            // see SeederConnectionId populated and are not promoted either. Every other
            // Leave call site delivers the demand; this one silently discarded it.
            var departure = editSessions.Leave(pageId, Context.ConnectionId);
            if (departure?.Demand is not null)
            {
                await SendReseedDemandAsync(pageId, departure.Demand);
            }

            throw;
        }

        if (outcome.SessionCreated)
        {
            ApiTelemetry.CoEditSessionsStarted.Add(1);
        }

        ApiTelemetry.RecordCoEditJoin(ApiTelemetry.CoEditJoined);

        // Group membership BEFORE the log snapshot: an update racing this join may
        // then be both replayed and relayed (harmless - Yjs updates are idempotent
        // under merge); the reverse order could lose one, which is not.
        await Groups.AddToGroupAsync(Context.ConnectionId, EditGroupName(pageId));
        var log = editSessions.GetLogSnapshot(pageId);

        return new EditSessionJoinResult(
            outcome.IsSeeder ? RoleSeeder : RoleJoiner, outcome.BaseRevisionNumber, log.ToArray());
    }

    /// <summary>
    /// Relay one opaque Yjs update to the session (event <c>UpdateReceived</c>) and
    /// append it to the late-joiner log. Membership-gated: a connection that never
    /// joined (or was evicted) gets the same silent nothing a refused presence join
    /// gives - there is no one to attribute the bytes to and no group it may write into.
    /// </summary>
    [NoAudit("Per-update audit would be a keystroke log - design.md §7/§8 co-editing replaces per-keystroke " +
        "events with session semantics: the canEdit-consuming act is the audited JoinEditSession, the departure is " +
        "the audited LeaveEditSession, and the content becomes durable only through updatePageContent, whose " +
        "page.edit row carries the session's contributors.")]
    public async Task PushUpdate(Guid pageId, byte[] update)
    {
        if (update.Length > coEditOptions.Value.UpdateMaxBytes)
        {
            ApiTelemetry.RecordCoEditRelay(ApiTelemetry.CoEditKindOversized, update.Length);
            return;
        }

        // Append (under the session lock) BEFORE relaying: a joiner snapshotting
        // between the two sees the update in the log and possibly also live -
        // idempotent, fine. Relay-then-append could leave a joiner with neither.
        var result = editSessions.AppendUpdate(pageId, Context.ConnectionId, update);
        if (!result.Accepted)
        {
            return;
        }

        ApiTelemetry.RecordCoEditRelay(ApiTelemetry.CoEditKindUpdate, update.Length);
        await Clients.OthersInGroup(EditGroupName(pageId)).SendAsync("UpdateReceived", pageId, update);

        if (result.Demand is not null)
        {
            await SendReseedDemandAsync(pageId, result.Demand);
        }
    }

    /// <summary>
    /// Relay one Yjs awareness update (carets, selections, editor identity — the §8
    /// "text carets are a feature of the CRDT layer" channel) to the session, event
    /// <c>AwarenessReceived</c>. Ephemeral by construction: never appended to the
    /// update log (a late joiner gets live awareness from peers within a heartbeat;
    /// replaying stale carets would be wrong, not just wasteful), never persisted,
    /// never audited — presence rules (§8), on the presence tier.
    /// </summary>
    [NoAudit("Ephemeral awareness (carets/selections) - the co-editing equivalent of presence, which design.md §8 " +
        "deliberately leaves unaudited; it is never logged or persisted, so there is no record to keep.")]
    public async Task PushAwareness(Guid pageId, byte[] awarenessUpdate)
    {
        if (awarenessUpdate.Length > coEditOptions.Value.AwarenessMaxBytes)
        {
            ApiTelemetry.RecordCoEditRelay(ApiTelemetry.CoEditKindOversized, awarenessUpdate.Length);
            return;
        }

        if (editSessions.GetMember(pageId, Context.ConnectionId) is null)
        {
            return;
        }

        ApiTelemetry.RecordCoEditRelay(ApiTelemetry.CoEditKindAwareness, awarenessUpdate.Length);
        await Clients.OthersInGroup(EditGroupName(pageId)).SendAsync("AwarenessReceived", pageId, awarenessUpdate);
    }

    /// <summary>
    /// The second half of the log-cap flow (see CoEditOptions.LogCapBytes): the
    /// member a <c>ReseedRequired(log_cap)</c> named saves via updatePageContent,
    /// then calls this with the full encoded Y.Doc state; the server swaps the log
    /// for that single snapshot. Honoured only from the designated connection while
    /// a reseed is pending; the new base revision is read from the page row here —
    /// the client's claim about what it saved is ignored on principle (the same
    /// no-client-claims rule contributors follow).
    /// </summary>
    [NoAudit("Log maintenance inside an already-audited session: the accompanying save audits as page.edit with " +
        "contributors, and the snapshot itself creates no new access or content fact - it re-encodes what the " +
        "session's audited members already share.")]
    public async Task ReseedEditSession(Guid pageId, byte[] fullState)
    {
        if (fullState.Length > coEditOptions.Value.SnapshotMaxBytes)
        {
            ApiTelemetry.RecordCoEditRelay(ApiTelemetry.CoEditKindOversized, fullState.Length);
            return;
        }

        var currentRevisionNumber = await db.Pages.AsNoTracking()
            .Where(p => p.Id == pageId)
            .Select(p => p.CurrentRevisionNumber)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        if (editSessions.ApplyReseed(pageId, Context.ConnectionId, fullState, currentRevisionNumber))
        {
            ApiTelemetry.RecordCoEditLogReset(ApiTelemetry.CoEditLogResetCapReseed);
        }
    }

    /// <summary>
    /// Explicit leave (route changes keep the connection alive - the same reason
    /// LeavePage exists). The departure always succeeds; its audit row is written
    /// after the fact and a failure there surfaces as a hub error without undoing
    /// the leave - a user who somehow cannot be audited must still be able to STOP
    /// editing (the same asymmetry §8 grants unwatching). Joining is where §7's
    /// fail-closed gate sits.
    /// </summary>
    [AuditAction(EditSessionAudit.LeftAction)]
    public async Task LeaveEditSession(Guid pageId)
    {
        var departure = editSessions.Leave(pageId, Context.ConnectionId);
        if (departure is null)
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, EditGroupName(pageId));
        if (departure.Demand is not null)
        {
            await SendReseedDemandAsync(pageId, departure.Demand);
        }

        await RecordLeftAsync(departure, EditSessionAudit.LeftReasonLeft, Context.ConnectionAborted);
    }

    private async Task HandleEditSessionDisconnectAsync()
    {
        foreach (var departure in editSessions.RemoveConnection(Context.ConnectionId))
        {
            // No group removal needed - SignalR drops a closed connection from its
            // groups itself; only the registry and the audit record need tending.
            if (departure.Demand is not null)
            {
                await SendReseedDemandAsync(departure.PageId, departure.Demand);
            }

            await RecordLeftAsync(departure, EditSessionAudit.LeftReasonDisconnected, CancellationToken.None);
        }
    }

    private Task SendReseedDemandAsync(Guid pageId, ReseedDemand demand) =>
        Clients.Client(demand.ConnectionId)
            .SendAsync("ReseedRequired", pageId, demand.BaseRevisionNumber, demand.Reason);

    private async Task RecordLeftAsync(EditSessionDeparture departure, string reason, CancellationToken cancellationToken)
    {
        var spaceKey = await db.Pages.AsNoTracking()
            .Where(p => p.Id == departure.PageId)
            .Select(p => p.Space!.Key)
            .FirstOrDefaultAsync(cancellationToken);

        await EditSessionAudit.RecordAsync(
            db, departure.Member.UserId, EditSessionAudit.LeftAction, AuditOutcome.Success,
            departure.PageId, spaceKey, EditSessionAudit.ReasonDetails(reason),
            departure.Member.ConnectionId, departure.Member.ClientIp, cancellationToken);
    }
}
