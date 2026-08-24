using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// design.md §8: "connections evicted and re-authorized when rules change — the same
/// cache-invalidation signal that refreshes the rule engine (§6.7)." Called from
/// Mutation.AccessRules.cs after any successful rule change.
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
        await ReauthorizeEditSessionsAsync(cancellationToken);

        var connections = registry.GetAllPageConnections();
        if (connections.Count == 0)
        {
            return;
        }

        // design.md §15: the sweep re-checks every open presence connection, so its cost
        // scales with concurrent viewers - worth a span with the size of the working set
        // it swept and how many it evicted. No connection ids, no user ids, no page ids
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

        var affectedPages = new HashSet<Guid>();
        var evicted = 0;
        foreach (var (pageId, connectionId, principal) in connections)
        {
            // ValueOrNull: this sweep only needs "still viewable or not" - the eviction
            // itself is a consequence of a rule change (whose mutation was audited),
            // not a user's read request, so there is no denied *read* to audit here.
            var page = (await pageReadService.GetPageAsync(pageId, principal, cancellationToken)).ValueOrNull();
            if (page is not null)
            {
                continue;
            }

            // No new "evicted" event: the frontend's proposed contract doesn't include
            // one, and there is nothing to say beyond what the next ViewersChanged
            // already implies - the evicted connection simply stops being a member of
            // this page's group and stops receiving anything scoped to it, the same
            // "absent, not forbidden" shape everywhere else in this schema.
            registry.LeavePage(pageId, connectionId);
            await hubContext.Groups.RemoveFromGroupAsync(connectionId, NotificationsHub.GroupName(pageId), cancellationToken);
            affectedPages.Add(pageId);
            evicted++;
        }

        ApiTelemetry.PresenceEvictions.Add(evicted);
        activity?.SetTag(ApiTelemetry.PresenceEvictedCountTag, evicted);

        foreach (var pageId in affectedPages)
        {
            var views = registry.GetViewers(pageId)
                .Select(v => new { userId = v.UserId, displayName = v.DisplayName, colour = v.Colour })
                .ToArray();
            await hubContext.Clients.Group(NotificationsHub.GroupName(pageId)).SendAsync("ViewersChanged", views, cancellationToken);
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
    private async Task ReauthorizeEditSessionsAsync(CancellationToken cancellationToken)
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
            var facts = await permissionReadService.GetPermissionFactsAsync([pageId], member.Principal, cancellationToken);
            if (facts.TryGetValue(pageId, out var fact) && fact.Permission.CanEdit)
            {
                continue;
            }

            var departure = editSessions.Leave(pageId, member.ConnectionId);
            if (departure is null)
            {
                continue; // already gone (raced a disconnect)
            }

            await hubContext.Groups.RemoveFromGroupAsync(
                member.ConnectionId, NotificationsHub.EditGroupName(pageId), cancellationToken);
            await hubContext.Clients.Client(member.ConnectionId)
                .SendAsync("EvictedFromEditSession", pageId, cancellationToken);

            if (departure.Demand is not null)
            {
                await hubContext.Clients.Client(departure.Demand.ConnectionId).SendAsync(
                    "ReseedRequired", pageId, departure.Demand.BaseRevisionNumber, departure.Demand.Reason, cancellationToken);
            }

            var spaceKey = await db.Pages.AsNoTracking()
                .Where(p => p.Id == pageId)
                .Select(p => p.Space!.Key)
                .FirstOrDefaultAsync(cancellationToken);

            await EditSessionAudit.RecordAsync(
                db, member.UserId, EditSessionAudit.LeftAction, AuditOutcome.Success,
                pageId, spaceKey, EditSessionAudit.ReasonDetails(EditSessionAudit.LeftReasonEvicted),
                member.ConnectionId, member.ClientIp, cancellationToken);

            evicted++;
        }

        ApiTelemetry.CoEditEvictions.Add(evicted);
    }
}
