using System.Collections.Concurrent;
using RocketWiki.Core.Access;

namespace RocketWiki.Api.RealTime;

public sealed class RealtimeConnectionRegistry : IRealtimeConnectionRegistry
{
    private sealed record ConnectionInfo(Guid UserId, Principal Principal);

    /// <summary>
    /// The single source of truth for who is connected and as whom. There used to be a
    /// second map (<c>user → principal</c>) kept in step by hand, and keeping it in step
    /// is what could not be done safely: unregister scanned this dictionary for another
    /// connection belonging to the same user and, finding none, deleted the user's entry —
    /// but the scan and the delete are not atomic, so a SignalR reconnect publishing its
    /// principal between them had that fresh entry deleted by the OLD connection's
    /// teardown. The user stayed connected while <c>GetConnectedPrincipal</c> answered
    /// null for the rest of the session, so <see cref="INotificationDispatcher"/> routed
    /// them down the offline branch: no live push, silently, for good.
    ///
    /// <para>Two intermediate designs did not fix it and are worth recording, because both
    /// look right. Re-checking after the delete just moves the window. Keying the second
    /// map per connection (<c>user → (connection → principal)</c>) fixes the entry-level
    /// race but not the bucket-level one: removing a bucket that has just become empty
    /// races a reconnect that is about to write into it.</para>
    ///
    /// <para>So there is no second map. <c>GetConnectedPrincipal</c> derives its answer
    /// from this one, which makes "a principal exists exactly while a connection does"
    /// true by construction rather than by ordering. It costs a scan per lookup over a
    /// live-bounded set (people with a page open right now) — the same order of work the
    /// eviction sweep already does, and the correct trade for an invariant that cannot
    /// otherwise be held without a lock.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    /// <summary>
    /// Keyed by ROOM KEY, not page id. Presence began as a page-only feature so the room
    /// was the page; site-wide presence makes any screen a room (see
    /// <see cref="PresenceRoom"/>), and the registry deliberately knows nothing about what
    /// a key means — the hub authorizes before anything reaches here, so a key in this
    /// dictionary is one that already passed its own gate. Keeping the classification out
    /// of the registry is what stops it acquiring a second, weaker opinion about access.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PresenceViewer>> _viewersByRoom = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _roomsByConnection = new();

    public void RegisterConnection(string connectionId, Guid userId, Principal principal) =>
        _connections[connectionId] = new ConnectionInfo(userId, principal);

    public IReadOnlyList<string> UnregisterConnection(string connectionId)
    {
        // Removing this connection is the whole of the user-principal bookkeeping now:
        // GetConnectedPrincipal reads _connections directly, so a still-open tab (or a
        // reconnect that landed a moment ago) keeps answering without anything here having
        // to notice it exists.
        _connections.TryRemove(connectionId, out _);

        if (!_roomsByConnection.TryRemove(connectionId, out var rooms))
        {
            return [];
        }

        var affectedRoomKeys = rooms.Keys.ToList();
        foreach (var roomKey in affectedRoomKeys)
        {
            LeaveRoom(roomKey, connectionId);
        }

        return affectedRoomKeys;
    }

    /// <summary>
    /// Any one of this user's live connections' principals — they are all built from that
    /// user's own token, so which connection answers does not matter. Null exactly when
    /// the user has no open connection, and "exactly" is the point: derived from
    /// <c>_connections</c> rather than mirrored into a second map, so there is no
    /// interleaving in which a connected user has no principal. See that field's doc.
    /// </summary>
    public Principal? GetConnectedPrincipal(Guid userId)
    {
        foreach (var info in _connections.Values)
        {
            if (info.UserId == userId)
            {
                return info.Principal;
            }
        }

        return null;
    }

    /// <summary>
    /// The most rooms one connection may hold <b>at once</b>. A well-behaved client is in
    /// exactly one — it leaves the old room as it navigates — and the worst legitimate
    /// transient is two, mid-navigation. Eight is deliberate headroom over that; the point
    /// is that the number is finite, not that it is tight.
    ///
    /// <para>It exists because room keys are client-supplied and, for <c>site:</c> paths,
    /// deliberately not validated against any route list — a screen name identifies no
    /// resource, so there is nothing to authorize, and open-ended routes (docs topics)
    /// must work. That is the right call for access control, and it leaves cardinality
    /// unbounded, which before this feature it was not: presence rooms were bounded to
    /// real viewable pages. Capping per connection restores a bound over every room type
    /// at once and couples to no route list.</para>
    ///
    /// <para><b>This is the ACUTE bound only</b> — how much one connection can hold right
    /// now. It does not address accumulation over time: <see cref="LeaveRoom"/> leaves an
    /// emptied bucket in place, so rooms joined and left still accrue keys for the life of
    /// the process. That is the race-safe empty-bucket removal tracked as task #37, and it
    /// is deliberately not attempted here: removing a bucket races a joiner and can drop a
    /// live viewer, which is a worse failure than a dictionary entry.</para>
    /// </summary>
    internal const int MaxRoomsPerConnection = 8;

    public bool JoinRoom(string roomKey, PresenceViewer viewer)
    {
        var rooms = _roomsByConnection.GetOrAdd(viewer.ConnectionId, static _ => new ConcurrentDictionary<string, byte>());

        // Re-joining a room already held is idempotent and never counts against the cap:
        // a client retrying a join it already made must not be refused for it.
        if (!rooms.ContainsKey(roomKey) && rooms.Count >= MaxRoomsPerConnection)
        {
            return false;
        }

        _viewersByRoom.GetOrAdd(roomKey, static _ => new ConcurrentDictionary<string, PresenceViewer>())[viewer.ConnectionId] = viewer;
        rooms[roomKey] = 0;
        return true;
    }

    public void LeaveRoom(string roomKey, string connectionId)
    {
        if (_viewersByRoom.TryGetValue(roomKey, out var viewers))
        {
            viewers.TryRemove(connectionId, out _);

            // The now-empty bucket is deliberately LEFT IN PLACE, and this is a known
            // leak: _viewersByRoom grows by one entry per room ever joined and never
            // shrinks. Removing it here is not safe with this structure — JoinRoom
            // reaches the bucket through GetOrAdd, so between "is it empty" and the
            // removal a joiner can populate the very instance being removed, and the
            // atomic key/value TryRemove does not help because the instance is
            // unchanged; only its contents are. A test written for this
            // (AJoinRacingTheLastLeave_IsNotDropped) does reproduce the drop.
            //
            // Doing it properly means what EditSessionRegistry does for sessions: a
            // Removed flag on the bucket plus a retry in the join path, or a periodic
            // sweep of buckets empty for some interval. That is a design change, not a
            // tidy-up, and dropping a live viewer is a worse outcome than an entry per
            // page in a dictionary — so it is recorded here rather than half-done.
        }

        if (_roomsByConnection.TryGetValue(connectionId, out var rooms))
        {
            rooms.TryRemove(roomKey, out _);
        }
    }

    public IReadOnlyList<PresenceViewer> GetViewers(string roomKey) =>
        _viewersByRoom.TryGetValue(roomKey, out var viewers) ? viewers.Values.ToList() : [];

    /// <summary>
    /// Every viewer currently present on any page, principal attached where one is known.
    ///
    /// <para>A viewer with no <c>_connections</c> entry yields a <b>null</b> principal
    /// rather than being dropped. This used to inner-join the two maps, so such a viewer
    /// was skipped — never re-checked, never evicted — and the case is reachable:
    /// <c>JoinPage</c> registers presence without repairing a missing connection record,
    /// and the hub only calls <c>RegisterConnection</c> when the local User row already
    /// exists. Silently exempting the one viewer whose identity the registry cannot state
    /// is backwards; the caller evicts them (§6.7).</para>
    /// </summary>
    public IReadOnlyList<(string RoomKey, string ConnectionId, Principal? Principal)> GetAllRoomConnections()
    {
        var result = new List<(string, string, Principal?)>();
        foreach (var (roomKey, viewers) in _viewersByRoom)
        {
            foreach (var connectionId in viewers.Keys)
            {
                result.Add((
                    roomKey,
                    connectionId,
                    _connections.TryGetValue(connectionId, out var info) ? info.Principal : null));
            }
        }

        return result;
    }
}
