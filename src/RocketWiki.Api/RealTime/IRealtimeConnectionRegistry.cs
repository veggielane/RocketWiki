using RocketWiki.Core.Access;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// One viewer in a presence room, as presence broadcasts them. <paramref name="HasAvatar"/>
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
/// 1. Who is present in which ROOM, backing the presence groups (design.md §8:
///    "page-scoped groups... authorized at join time... evicted and re-authorized when
///    rules change"). Rooms generalize that: a page is one kind of room, a space and a
///    global route are two more, and each carries its own gate.
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

    /// <summary>Called once per connection (Hub.OnDisconnectedAsync). Returns every ROOM the connection was still present in, so the caller can rebroadcast ViewersChanged for each.</summary>
    IReadOnlyList<string> UnregisterConnection(string connectionId);

    /// <summary>Null if this user has no currently-open connection.</summary>
    Principal? GetConnectedPrincipal(Guid userId);

    /// <summary>
    /// Records presence in a room. <b>The key is opaque here on purpose.</b> Rooms carry
    /// different authorization by type (see <see cref="PresenceRoom"/>) and the hub
    /// applies it before calling this — giving the registry any opinion about what a key
    /// means would be a second, weaker place for an access decision to live.
    /// </summary>
    /// <returns>False when this connection already holds the maximum number of rooms
    /// and this would be a new one — the caller must then treat the join as refused and
    /// must NOT add the connection to the SignalR group, or the client would receive a
    /// room the registry has no record of it being in.</returns>
    bool JoinRoom(string roomKey, PresenceViewer viewer);

    void LeaveRoom(string roomKey, string connectionId);

    IReadOnlyList<PresenceViewer> GetViewers(string roomKey);

    /// <summary>
    /// Every (room, connection, principal) triple currently present anywhere — the
    /// working set <see cref="IPresenceRuleChangeNotifier"/> re-checks on an access change.
    ///
    /// <para><b>Principal is nullable, and a null one means "evict".</b> A viewer can be
    /// present in a room with no matching connection entry — <c>JoinRoom</c> registers
    /// presence without repairing a missing connection record, and the hub only calls
    /// <c>RegisterConnection</c> when the local User row already exists. This used to
    /// inner-join the two maps and silently drop such a viewer from the working set, which
    /// meant the one case where the registry cannot say who someone is was also the one
    /// case where they were never re-checked. §6.7's shape is the opposite: a viewer whose
    /// principal cannot be resolved is exactly who should lose the group.</para>
    /// </summary>
    IReadOnlyList<(string RoomKey, string ConnectionId, Principal? Principal)> GetAllRoomConnections();
}
