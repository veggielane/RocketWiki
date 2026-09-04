using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Reads;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// design.md §8: "connections evicted and re-authorized when rules change — the same
/// cache-invalidation signal that refreshes the rule engine (§6.7)."
///
/// <para><b>"Rules" means every input to canView/canEdit, not just AccessRule rows.</b>
/// This used to be called only from Mutation.AccessRules.cs, which covered one of the
/// three things the decision is computed from (§6.4/§21: space grants, the restriction
/// chain, AND the protective marking). So a page re-marked out of a joined co-editor's
/// reach left them in the SignalR group — still receiving <c>UpdateReceived</c>,
/// which is page content in CRDT form, and still able to <c>PushUpdate</c>. It is now
/// called after every mutation that can change the answer: rule create/update/delete,
/// <c>setPageMarking</c> (§21 gates views "on every read path, exactly as a page
/// restriction does"), <c>movePage</c> (a new AncestorPath means a different inherited
/// restriction chain), <c>deletePage</c>, and <c>archiveSpace</c>.</para>
///
/// <para><b>The caller's cancellation token is deliberately ignored.</b> The sweep runs
/// after the change has committed, so abandoning it half-done leaves an authorization
/// change durably applied in the database and only partially applied to live sessions —
/// fail-open, with no retry and no queue. An admin's browser going away is not a decision
/// about whether the eviction should finish. Individual failures inside the sweep are
/// contained per connection and fail closed (evict) rather than unwinding the loop.</para>
///
/// Deliberately re-checks EVERY open presence connection rather than computing which
/// pages one rule change could possibly affect: restrictions accumulate down the page
/// tree, so a change to an ancestor's restriction can affect an unbounded set of
/// descendants, and correctly enumerating "every current or future descendant of this
/// page" is real tree-walking work for what should be a rare, non-hot-path event.
/// Presence connections are a small, live-bounded set (only people with a page open
/// right now), and canView is cheap by design (§6.7: "pure in-process boolean logic"),
/// so the simple, obviously-correct sweep beats a more surgical one that has to get
/// the blast radius exactly right to avoid under-evicting.
///
/// The same sweep covers EDIT sessions (design.md §8 co-editing) against canEdit: an
/// editor whose rules changed loses the relay, not just the viewer list. Unlike a
/// presence eviction (silent — the next ViewersChanged says everything), an evicted
/// editor gets an explicit <c>EvictedFromEditSession</c> event: silently cutting the
/// relay would leave them typing into a local doc whose updates go nowhere, and the
/// event reveals nothing they didn't already legitimately have — they were IN the
/// session; what changed is that they may no longer be, which is exactly the fact
/// they must act on (§6.7's absent-not-forbidden protects existence from those with
/// no right to know it, not this).
/// </summary>
public interface IPresenceRuleChangeNotifier
{
    Task NotifyRulesChangedAsync(CancellationToken cancellationToken);
}

public sealed class PresenceRuleChangeNotifier(
    IRealtimeConnectionRegistry registry,
    IEditSessionRegistry editSessions,
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationsHub> hubContext) : IPresenceRuleChangeNotifier
{
    public async Task NotifyRulesChangedAsync(CancellationToken cancellationToken)
    {
        // design.md §6.7/§8: the sweep runs to completion regardless of the CALLER's
        // token. It used to thread the admin's request token through every step, so an
        // admin whose HTTP connection dropped mid-sweep left every not-yet-visited
        // connection joined with the restriction already committed — an authorization
        // change that had durably taken effect in the database but only partially in the
        // live session state, with no retry, no queue and no signal. That is fail-OPEN on
        // the one path whose entire job is closing access, and the cancellation it obeyed
        // was not even a decision about this work.
        //
        // The parameter stays for interface compatibility and is deliberately unused; see
        // the interface doc.
        _ = cancellationToken;

        await ReauthorizeEditSessionsAsync();

        var connections = registry.GetAllRoomConnections();
        if (connections.Count == 0)
        {
            return;
        }

        // design.md §15: the sweep re-checks every open presence connection, so its cost
        // scales with concurrent viewers - worth a span with the size of the working set
        // it swept and how many it evicted. No connection ids, no user ids, no room keys
        // per eviction: this method's whole job is deciding who may no longer see what,
        // and recording that per-subject in a trace is precisely the second, unregulated
        // access record §15 exists to prevent.
        using var activity = ApiTelemetry.ActivitySource.StartActivity(
            ApiTelemetry.PresenceReauthorizeSpan, ActivityKind.Internal);
        activity?.SetTag(ApiTelemetry.PresenceConnectionCountTag, connections.Count);

        // A fresh scope, not the caller's own DbContext: this runs after a GraphQL
        // mutation resolver has already finished its own unit of work, and
        // IPageReadService needs its own scoped RocketWikiDbContext to see the rule
        // change that was just committed.
        using var scope = scopeFactory.CreateScope();
        var pageReadService = scope.ServiceProvider.GetRequiredService<IPageReadService>();

        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var affectedRooms = new HashSet<string>(StringComparer.Ordinal);
        var evicted = 0;
        foreach (var (roomKey, connectionId, principal) in connections)
        {
            bool stillViewable;
            if (principal is null)
            {
                // The registry knows this connection is present but cannot say who it is
                // (see GetAllRoomConnections). Unresolvable identity is not a reason to
                // leave someone in a page group — it is the clearest reason to remove
                // them.
                stillViewable = false;
            }
            else
            {
                try
                {
                    // ValueOrNull: this sweep only needs "still viewable or not" - the
                    // eviction itself is a consequence of an access change (whose mutation
                    // was audited), not a user's read request, so there is no denied
                    // *read* to audit here.
                    // Re-checked BY ROOM TYPE, with the same gate the join applied. A
                    // page room re-checks canView; a space room re-checks the space
                    // role, which is what makes revoking a grant evict the space
                    // browser too; a site room has no resource behind it, so no access
                    // change can make it unviewable and it is never evicted here.
                    //
                    // A key that no longer parses evicts. The registry only ever
                    // receives keys the hub already parsed, so that is unreachable
                    // today - and unreachable is exactly when a default must fail
                    // closed rather than assume.
                    stillViewable = PresenceRoom.TryParse(roomKey, out var room) && room switch
                    {
                        PresenceRoom.Page page =>
                            (await pageReadService.GetPageAsync(page.PageId, principal, CancellationToken.None))
                                .ValueOrNull() is not null,
                        PresenceRoom.Space space =>
                            await SpaceReads.GetViewableSpaceByKeyAsync(
                                db, principal, space.SpaceKey, CancellationToken.None) is SpaceReadResult.Found,
                        PresenceRoom.Site => true,
                        _ => false,
                    };
                }
                catch (Exception)
                {
                    // One connection's check failing must not abandon the rest of the
                    // sweep — that is how a durably-applied restriction ends up only
                    // partially enforced. Fail closed for this connection and carry on.
                    stillViewable = false;
                }
            }

            if (stillViewable)
            {
                continue;
            }

            // No new "evicted" event: the frontend's proposed contract doesn't include
            // one, and there is nothing to say beyond what the next ViewersChanged
            // already implies - the evicted connection simply stops being a member of
            // this page's group and stops receiving anything scoped to it, the same
            // "absent, not forbidden" shape everywhere else in this schema.
            //
            // Registry first, then the SignalR group: the group removal is the half that
            // actually stops data reaching the client, so it must not be skipped because
            // the registry call threw.
            registry.LeaveRoom(roomKey, connectionId);
            try
            {
                await hubContext.Groups.RemoveFromGroupAsync(
                    connectionId, roomKey, CancellationToken.None);
            }
            catch (Exception)
            {
                // A dead connection is the ordinary reason this throws, and it is already
                // the outcome we wanted. Anything else must still not stop the sweep.
            }

            affectedRooms.Add(roomKey);
            evicted++;
        }

        ApiTelemetry.PresenceEvictions.Add(evicted);
        activity?.SetTag(ApiTelemetry.PresenceEvictedCountTag, evicted);

        foreach (var roomKey in affectedRooms)
        {
            var views = registry.GetViewers(roomKey)
                .Select(v => new { userId = v.UserId, displayName = v.DisplayName, colour = v.Colour, hasAvatar = v.HasAvatar })
                .ToArray();
            await hubContext.Clients.Group(roomKey)
                .SendAsync("ViewersChanged", views, CancellationToken.None);
        }
    }

    /// <summary>
    /// The edit-session half of the sweep: every (page, member) pair re-checked
    /// against canEdit through the same calculator path the join used. Evicted
    /// members lose registry membership, the SignalR group (the relay), and receive
    /// EvictedFromEditSession; the departure is recorded as page.edit_session.left
    /// with reason "evicted" (§7 - the join row was written, so the session record
    /// closes honestly; the eviction's CAUSE is the already-audited rule change).
    /// Same telemetry stance as presence: an aggregate eviction count, no per-subject
    /// trace facts.
    /// </summary>
    private async Task ReauthorizeEditSessionsAsync()
    {
        var members = editSessions.GetAllMembers();
        if (members.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var permissionReadService = scope.ServiceProvider.GetRequiredService<IPagePermissionReadService>();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var evicted = 0;
        foreach (var (pageId, member) in members)
        {
            bool mayStillEdit;
            try
            {
                var facts = await permissionReadService.GetPermissionFactsAsync(
                    [pageId], member.Principal, CancellationToken.None);
                mayStillEdit = facts.TryGetValue(pageId, out var fact) && fact.Permission.CanEdit;
            }
            catch (Exception)
            {
                // Fail closed for this member, and do not abandon the others: a member
                // still relaying CRDT updates for a page they may no longer edit is the
                // outcome this sweep exists to prevent.
                mayStillEdit = false;
            }

            if (mayStillEdit)
            {
                continue;
            }

            var departure = editSessions.Leave(pageId, member.ConnectionId);
            if (departure is null)
            {
                continue; // already gone (raced a disconnect)
            }

            // The group removal is what actually closes the relay, so it comes first and
            // its failure never skips the rest.
            try
            {
                await hubContext.Groups.RemoveFromGroupAsync(
                    member.ConnectionId, NotificationsHub.EditGroupName(pageId), CancellationToken.None);
                await hubContext.Clients.Client(member.ConnectionId)
                    .SendAsync("EvictedFromEditSession", pageId, CancellationToken.None);

                if (departure.Demand is not null)
                {
                    await hubContext.Clients.Client(departure.Demand.ConnectionId).SendAsync(
                        "ReseedRequired", pageId, departure.Demand.BaseRevisionNumber, departure.Demand.Reason,
                        CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // A dead connection throws here and is already evicted as far as it
                // matters. Never let it stop the sweep.
            }

            evicted++;

            // §7's row for the departure. Written LAST and never allowed to unwind the
            // loop: the member has already lost the session and the group by this point,
            // so an audit-write failure that propagated would turn a bookkeeping fault
            // into an authorization one, leaving every later member un-evicted. This is
            // the same exception EditSessionAudit already states for disconnect-time
            // rows — nothing can un-evict someone, so the insert's failure cannot be made
            // to fail the action, only to be reported.
            try
            {
                var spaceKey = await db.Pages.AsNoTracking()
                    .Where(p => p.Id == pageId)
                    .Select(p => p.Space!.Key)
                    .FirstOrDefaultAsync(CancellationToken.None);

                await EditSessionAudit.RecordAsync(
                    db, member.UserId, EditSessionAudit.LeftAction, AuditOutcome.Success,
                    pageId, spaceKey, EditSessionAudit.ReasonDetails(EditSessionAudit.LeftReasonEvicted),
                    member.ConnectionId, member.ClientIp, CancellationToken.None);
            }
            catch (Exception)
            {
                // Counted below regardless: the eviction happened whether or not its row did.
                db.ChangeTracker.Clear();
            }
        }

        ApiTelemetry.CoEditEvictions.Add(evicted);
    }
}
