using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Services;

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
/// </summary>
public interface IPresenceRuleChangeNotifier
{
    Task NotifyRulesChangedAsync(CancellationToken cancellationToken);
}

public sealed class PresenceRuleChangeNotifier(
    IRealtimeConnectionRegistry registry,
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationsHub> hubContext) : IPresenceRuleChangeNotifier
{
    public async Task NotifyRulesChangedAsync(CancellationToken cancellationToken)
    {
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
        activity?.SetTag("rocketwiki.presence.connection_count", connections.Count);

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
        activity?.SetTag("rocketwiki.presence.evicted_count", evicted);

        foreach (var pageId in affectedPages)
        {
            var views = registry.GetViewers(pageId)
                .Select(v => new { userId = v.UserId, displayName = v.DisplayName, colour = v.Colour })
                .ToArray();
            await hubContext.Clients.Group(NotificationsHub.GroupName(pageId)).SendAsync("ViewersChanged", views, cancellationToken);
        }
    }
}
