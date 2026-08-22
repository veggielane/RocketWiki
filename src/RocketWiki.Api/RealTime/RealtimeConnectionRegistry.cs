using System.Collections.Concurrent;
using RocketWiki.Core.Access;

namespace RocketWiki.Api.RealTime;

public sealed class RealtimeConnectionRegistry : IRealtimeConnectionRegistry
{
    private sealed record ConnectionInfo(Guid UserId, Principal Principal);

    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private readonly ConcurrentDictionary<Guid, Principal> _connectedPrincipalsByUser = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, PresenceViewer>> _viewersByPage = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _pagesByConnection = new();

    public void RegisterConnection(string connectionId, Guid userId, Principal principal)
    {
        _connections[connectionId] = new ConnectionInfo(userId, principal);
        _connectedPrincipalsByUser[userId] = principal;
    }

    public IReadOnlyList<Guid> UnregisterConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var info))
        {
            // Only drop the user-level entry if no OTHER connection for the same user
            // remains (multiple tabs) - otherwise a still-open tab would silently lose
            // notification delivery the instant a second tab for the same user closes.
            if (!_connections.Values.Any(c => c.UserId == info.UserId))
            {
                _connectedPrincipalsByUser.TryRemove(info.UserId, out _);
            }
        }

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

    public Principal? GetConnectedPrincipal(Guid userId) =>
        _connectedPrincipalsByUser.TryGetValue(userId, out var principal) ? principal : null;

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
        }

        if (_pagesByConnection.TryGetValue(connectionId, out var pages))
        {
            pages.TryRemove(pageId, out _);
        }
    }

    public IReadOnlyList<PresenceViewer> GetViewers(Guid pageId) =>
        _viewersByPage.TryGetValue(pageId, out var viewers) ? viewers.Values.ToList() : [];

    public IReadOnlyList<(Guid PageId, string ConnectionId, Principal Principal)> GetAllPageConnections()
    {
        var result = new List<(Guid, string, Principal)>();
        foreach (var (pageId, viewers) in _viewersByPage)
        {
            foreach (var connectionId in viewers.Keys)
            {
                if (_connections.TryGetValue(connectionId, out var info))
                {
                    result.Add((pageId, connectionId, info.Principal));
                }
            }
        }

        return result;
    }
}
