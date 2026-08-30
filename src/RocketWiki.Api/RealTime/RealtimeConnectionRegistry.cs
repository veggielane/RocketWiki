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
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, PresenceViewer>> _viewersByPage = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _pagesByConnection = new();

    public void RegisterConnection(string connectionId, Guid userId, Principal principal) =>
        _connections[connectionId] = new ConnectionInfo(userId, principal);

    public IReadOnlyList<Guid> UnregisterConnection(string connectionId)
    {
        // Removing this connection is the whole of the user-principal bookkeeping now:
        // GetConnectedPrincipal reads _connections directly, so a still-open tab (or a
        // reconnect that landed a moment ago) keeps answering without anything here having
        // to notice it exists.
        _connections.TryRemove(connectionId, out _);

        if (!_pagesByConnection.TryRemove(connectionId, out var pages))
        {
            return [];
        }

        var affectedPageIds = pages.Keys.ToList();
        foreach (var pageId in affectedPageIds)
        {
            LeavePage(pageId, connectionId);
        }

        return affectedPageIds;
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

    public void JoinPage(Guid pageId, PresenceViewer viewer)
    {
        _viewersByPage.GetOrAdd(pageId, static _ => new ConcurrentDictionary<string, PresenceViewer>())[viewer.ConnectionId] = viewer;
        _pagesByConnection.GetOrAdd(viewer.ConnectionId, static _ => new ConcurrentDictionary<Guid, byte>())[pageId] = 0;
    }

    public void LeavePage(Guid pageId, string connectionId)
    {
        if (_viewersByPage.TryGetValue(pageId, out var viewers))
        {
            viewers.TryRemove(connectionId, out _);

            // The now-empty bucket is deliberately LEFT IN PLACE, and this is a known
            // leak: _viewersByPage grows by one entry per page ever viewed and never
            // shrinks. Removing it here is not safe with this structure — JoinPage
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

        if (_pagesByConnection.TryGetValue(connectionId, out var pages))
        {
            pages.TryRemove(pageId, out _);
        }
    }

    public IReadOnlyList<PresenceViewer> GetViewers(Guid pageId) =>
        _viewersByPage.TryGetValue(pageId, out var viewers) ? viewers.Values.ToList() : [];

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
    public IReadOnlyList<(Guid PageId, string ConnectionId, Principal? Principal)> GetAllPageConnections()
    {
        var result = new List<(Guid, string, Principal?)>();
        foreach (var (pageId, viewers) in _viewersByPage)
        {
            foreach (var connectionId in viewers.Keys)
            {
                result.Add((
                    pageId,
                    connectionId,
                    _connections.TryGetValue(connectionId, out var info) ? info.Principal : null));
            }
        }

        return result;
    }
}
