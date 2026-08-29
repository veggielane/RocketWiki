using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Services;
using RocketWiki.Data;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// design.md §8: "/hubs/notifications" — one hub carries both durable per-user
/// notifications (<see cref="INotificationDispatcher"/>, pushed to <c>user:{id}</c>
/// groups) and ephemeral page-scoped presence (this class's own methods),
/// deliberately: "the same hub is the intended transport for CRDT co-editing later...
/// one authenticated real-time transport beats two."
///
/// Method and event names below (<c>JoinPage</c>/<c>LeavePage</c>/<c>PointerMove</c>,
/// <c>Notification</c>/<c>ViewersChanged</c>/<c>PointerMoved</c>) are the frontend's
/// own proposed contract, adopted as-is — team direction was explicit: adopt or
/// negotiate, never silently diverge.
///
/// Cannot reuse <see cref="ICurrentPrincipalAccessor"/>/<see cref="IActingUserAccessor"/>
/// here: both are built on <c>IHttpContextAccessor</c>, which is not reliably populated
/// for a Hub method invocation over an already-established connection — the same class
/// of cross-DI-scope trap this project already hit once with Hot Chocolate resolvers
/// (see <c>ICurrentAuditContextAccessor</c>'s doc), just in a new place.
/// <c>HubCallerContext.User</c> is SignalR's own reliable equivalent, so the Principal
/// is rebuilt fresh from it via <see cref="PrincipalBuilder"/> on every call instead.
/// </summary>
[Authorize]
public sealed partial class NotificationsHub : Hub
{
    // Explicit fields rather than a primary constructor: primary-constructor
    // parameters are only in scope in the partial declaration that declares them,
    // and the edit-session half of this hub lives in NotificationsHub.EditSessions.cs.
    private readonly IRealtimeConnectionRegistry registry;
    private readonly IEditSessionRegistry editSessions;
    private readonly IPageReadService pageReadService;
    private readonly IPagePermissionReadService pagePermissionReadService;
    private readonly IOptions<CoEditOptions> coEditOptions;
    private readonly RocketWikiDbContext db;

    public NotificationsHub(
        IRealtimeConnectionRegistry registry,
        IEditSessionRegistry editSessions,
        IPageReadService pageReadService,
        IPagePermissionReadService pagePermissionReadService,
        IOptions<CoEditOptions> coEditOptions,
        RocketWikiDbContext db)
    {
        this.registry = registry;
        this.editSessions = editSessions;
        this.pageReadService = pageReadService;
        this.pagePermissionReadService = pagePermissionReadService;
        this.coEditOptions = coEditOptions;
        this.db = db;
    }

    internal static string GroupName(Guid pageId) => $"page:{pageId}";
    internal static string UserGroupName(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var principal = PrincipalBuilder.Build(Context.User);
        if (principal is not null)
        {
            // By the time a page is open to establish this connection, the SPA has
            // already made at least one authenticated HTTP request (loading the page's
            // own data), which JIT-provisions the local User row (design.md §11.3) - so
            // a lookup here (rather than re-implementing JIT provisioning for a Hub
            // invocation) is expected to succeed in every real case. A connection from a
            // user with no local row yet simply gets no presence/notification delivery
            // until that happens, rather than provisioning one from this narrower path.
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Subject == principal.UserId, Context.ConnectionAborted);
            if (user is not null)
            {
                registry.RegisterConnection(Context.ConnectionId, user.Id, principal);
                await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(user.Id));
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var affectedPageIds = registry.UnregisterConnection(Context.ConnectionId);
        foreach (var pageId in affectedPageIds)
        {
            ApiTelemetry.RecordPresenceLeave(ApiTelemetry.PresenceLeaveDisconnected);
            await Clients.Group(GroupName(pageId)).SendAsync("ViewersChanged", ToPublicViews(pageId));
        }

        // Edit sessions (design.md §8 co-editing): depart every session this
        // connection was in - see NotificationsHub.EditSessions.cs.
        await HandleEditSessionDisconnectAsync();

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// design.md §8: join is authorized by canView, same as any other read. A page the
    /// caller cannot view simply never joins the group and never appears in
    /// ViewersChanged — the same "absent, not forbidden" shape as everywhere else
    /// (§6.7): a caller probing a restricted page id sees no different behaviour than
    /// probing a nonexistent one.
    /// </summary>
    [NoAudit("Presence is deliberately unaudited (design.md §8: 'no table, no audit rows'; " +
        "data-model.md: 'Presence has no table') - the page view itself is already audited, and presence adds no new record. " +
        "Contrast JoinEditSession, which IS audited: joining an edit session consumes canEdit and opens a content-bearing channel.")]
    public async Task JoinPage(Guid pageId)
    {
        var principal = PrincipalBuilder.Build(Context.User);
        if (principal is null)
        {
            ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceNoPrincipal);
            return;
        }

        // ValueOrNull: presence only needs "viewable or not". Denied joins are counted
        // below but deliberately not audit-rowed - presence joins aren't audited at all
        // yet (success or denial, no [AuditAction] exists for them), and recording only
        // the denials would make the audit log imply joins are covered when they aren't.
        var page = (await pageReadService.GetPageAsync(pageId, principal, Context.ConnectionAborted)).ValueOrNull();
        if (page is null)
        {
            // Counted, not distinguished to the caller: the three silent-return branches
            // in this method are indistinguishable over the wire on purpose (§6.7), and
            // an aggregate count with no page id and no user id keeps it that way while
            // still telling an operator which branch is firing.
            ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceNotViewable);
            return;
        }

        // HasAvatar as a correlated EXISTS in this same query — the join already had to
        // read the user row, so presence gains the flag without a second round trip.
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Subject == principal.UserId)
            .Select(u => new
            {
                u.Id,
                u.DisplayName,
                HasAvatar = db.UserAvatars.Any(a => a.UserId == u.Id),
            })
            .FirstOrDefaultAsync(Context.ConnectionAborted);
        if (user is null)
        {
            ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceNoLocalUser);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(pageId));
        registry.JoinPage(pageId, new PresenceViewer(
            Context.ConnectionId, user.Id, user.DisplayName, ColourFor(user.Id), user.HasAvatar));
        ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceJoined);

        await Clients.Group(GroupName(pageId)).SendAsync("ViewersChanged", ToPublicViews(pageId));
    }

    /// <summary>
    /// design.md §8: "leaving a page is a route change, not an unmount" — the client
    /// calls this explicitly on every in-app navigation away, rather than relying on
    /// <see cref="OnDisconnectedAsync"/> (which only fires when the connection itself
    /// closes, not on a route change that keeps the same connection alive).
    /// </summary>
    [NoAudit("Presence is deliberately unaudited (design.md §8) - see JoinPage.")]
    public async Task LeavePage(Guid pageId)
    {
        registry.LeavePage(pageId, Context.ConnectionId);
        ApiTelemetry.RecordPresenceLeave(ApiTelemetry.PresenceLeaveExplicit);
        await Clients.Group(GroupName(pageId)).SendAsync("ViewersChanged", ToPublicViews(pageId));
    }

    [NoAudit("Ephemeral presence broadcast, never persisted or audited (design.md §8) - see JoinPage.")]
    public Task PointerMove(Guid pageId, double x, double y)
    {
        var viewer = registry.GetViewers(pageId).FirstOrDefault(v => v.ConnectionId == Context.ConnectionId);
        if (viewer is null)
        {
            // Never joined this page (or already evicted) - nothing to attribute the
            // pointer to, and broadcasting under no identity would be exactly the
            // "content, never attributes, never more than display name/colour" leak
            // design.md §8 warns against in the other direction.
            return Task.CompletedTask;
        }

        return Clients.OthersInGroup(GroupName(pageId)).SendAsync("PointerMoved", new
        {
            userId = viewer.UserId,
            displayName = viewer.DisplayName,
            colour = viewer.Colour,
            x,
            y,
        });
    }

    private object[] ToPublicViews(Guid pageId) => registry.GetViewers(pageId)
        .Select(v => (object)new
        {
            userId = v.UserId,
            displayName = v.DisplayName,
            colour = v.Colour,
            hasAvatar = v.HasAvatar,
        })
        .ToArray();

    private static readonly string[] Palette =
        ["#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#42d4f4", "#f032e6", "#bfef45"];

    private static string ColourFor(Guid userId) => Palette[unchecked((uint)userId.GetHashCode()) % (uint)Palette.Length];
}
