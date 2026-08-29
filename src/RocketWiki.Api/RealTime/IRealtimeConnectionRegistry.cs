using RocketWiki.Core.Access;

namespace RocketWiki.Api.RealTime;

/// <summary>One viewer's public presence facts (design.md §8: "payloads carry display name and colour only — never attributes").</summary>
/// <summary>
/// One viewer on a page, as presence broadcasts them. <paramref name="HasAvatar"/>
/// rides along for the same reason <c>UserRef</c> carries it (design.md §19): without
/// it the SPA cannot tell "no avatar" from "not fetched yet", so it probes
/// <c>GET /users/{id}/avatar</c> and takes a 404 for every viewer who has never
/// uploaded one. It costs nothing to supply — the hub already loads the user row on
/// join, so this is a correlated EXISTS inside that same query, not a second round
/// trip.
/// </summary>
public sealed record PresenceViewer(
    string ConnectionId, Guid UserId, string DisplayName, string Colour, bool HasAvatar);

/// <summary>
/// Ephemeral, in-memory only (design.md §8: presence "no table, no audit rows") —
/// a singleton, keyed by SignalR ConnectionId, since Hot Chocolate/SignalR create a
/// fresh Hub instance per invocation and cannot hold this state themselves. Tracks two
/// related things:
///
/// 1. Who is viewing which page, backing the page-scoped presence groups (design.md
///    §8: "page-scoped groups... authorized at join time... evicted and re-authorized
///    when rules change").
/// 2. Every currently-connected user's live Principal, so <see cref="INotificationDispatcher"/>
///    can evaluate canView for a recipient without needing their token directly (which
///    it has no way to obtain outside that user's own request) — only currently-connected
///    recipients can be checked this way; see that dispatcher's own doc for the
///    offline-recipient gap this leaves.
///
/// Not more than <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>'s
/// own thread-safety is needed here — presence is approximate and self-healing on the
/// next broadcast in a way nothing durable could be.
/// </summary>
public interface IRealtimeConnectionRegistry
{
    /// <summary>Called once per connection (Hub.OnConnectedAsync), independent of any page join.</summary>
    void RegisterConnection(string connectionId, Guid userId, Principal principal);

    /// <summary>Called once per connection (Hub.OnDisconnectedAsync). Returns every page the connection was still present on, so the caller can rebroadcast ViewersChanged for each.</summary>
    IReadOnlyList<Guid> UnregisterConnection(string connectionId);

    /// <summary>Null if this user has no currently-open connection.</summary>
    Principal? GetConnectedPrincipal(Guid userId);

    void JoinPage(Guid pageId, PresenceViewer viewer);

    void LeavePage(Guid pageId, string connectionId);

    IReadOnlyList<PresenceViewer> GetViewers(Guid pageId);

    /// <summary>Every (page, connection, principal) triple currently present on any page — the working set <see cref="IPresenceRuleChangeNotifier"/> re-checks on a rule change.</summary>
    IReadOnlyList<(Guid PageId, string ConnectionId, Principal Principal)> GetAllPageConnections();
}
