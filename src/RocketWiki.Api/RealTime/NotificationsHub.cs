using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Telemetry;
using RocketWiki.Api.Reads;
using RocketWiki.Core.Access;
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
    private readonly PrincipalBuilder principalBuilder;

    public NotificationsHub(
        IRealtimeConnectionRegistry registry,
        IEditSessionRegistry editSessions,
        IPageReadService pageReadService,
        IPagePermissionReadService pagePermissionReadService,
        IOptions<CoEditOptions> coEditOptions,
        RocketWikiDbContext db,
        PrincipalBuilder principalBuilder)
    {
        this.registry = registry;
        this.editSessions = editSessions;
        this.pageReadService = pageReadService;
        this.pagePermissionReadService = pagePermissionReadService;
        this.coEditOptions = coEditOptions;
        this.db = db;
        // The same singleton builder the HTTP accessor uses (design.md §21.15): the hub
        // principal maps every configured selector claim exactly as a GraphQL request's
        // does, so a co-editor is admitted or evicted by the same gates on both paths.
        this.principalBuilder = principalBuilder;
    }

    /// <summary>The room key IS the SignalR group name — see <see cref="PresenceRoom"/>.
    /// Kept for the page case because callers outside this hub (the rule-change sweep)
    /// still name page groups directly.</summary>
    internal static string GroupName(Guid pageId) => $"page:{pageId}";
    internal static string UserGroupName(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var principal = principalBuilder.Build(Context.User);
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
        var affectedRoomKeys = registry.UnregisterConnection(Context.ConnectionId);
        foreach (var roomKey in affectedRoomKeys)
        {
            ApiTelemetry.RecordPresenceLeave(ApiTelemetry.PresenceLeaveDisconnected);
            await Clients.Group(roomKey).SendAsync("ViewersChanged", ToPublicViews(roomKey));
        }

        // Edit sessions (design.md §8 co-editing): depart every session this
        // connection was in - see NotificationsHub.EditSessions.cs.
        await HandleEditSessionDisconnectAsync();

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// design.md §8: join is authorized, always — what differs by room type is *how*.
    /// A page room is canView, exactly as <c>JoinPage</c> was; a space room needs a role
    /// in the space; a global route needs only a signed-in caller, which is safe solely
    /// because the route comes from a fixed allowlist and carries no identifier.
    ///
    /// <para>Every refusal is the same silent return — "absent, not forbidden" (§6.7).
    /// The space case is the one that matters most: a caller probing
    /// <c>space:SECRET</c> must not be able to tell an invisible space from one that does
    /// not exist, so both outcomes leave by the same branch, and the telemetry counter is
    /// deliberately the same for both too. A counter split by reason would reintroduce the
    /// distinction in the one place an operator could read it back out.</para>
    /// </summary>
    [NoAudit("Presence is deliberately unaudited (design.md §8: 'no table, no audit rows'; " +
        "data-model.md: 'Presence has no table') - the page view itself is already audited, and presence adds no new record. " +
        "Contrast JoinEditSession, which IS audited: joining an edit session consumes canEdit and opens a content-bearing channel.")]
    public async Task JoinRoom(string roomKey)
    {
        var principal = principalBuilder.Build(Context.User);
        if (principal is null)
        {
            ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceNoPrincipal);
            return;
        }

        // Parse before authorize: an unknown prefix, a malformed page id, or a site route
        // that is not on the allowlist never reaches a gate at all. There is no default
        // branch that treats an unrecognised key as permissible.
        if (!PresenceRoom.TryParse(roomKey, out var room))
        {
            ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceNotViewable);
            return;
        }

        if (!await IsRoomJoinableAsync(room, principal))
        {
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

        // Registry FIRST, group second. The registry owns the cap, and adding the
        // connection to the SignalR group before knowing whether the join was accepted
        // would leave a client receiving a room the registry says it is not in — which is
        // the one inconsistency the eviction sweep cannot repair, because the sweep walks
        // the registry.
        var joined = registry.JoinRoom(room.Key, new PresenceViewer(
            Context.ConnectionId, user.Id, user.DisplayName, ColourFor(user.Id), user.HasAvatar));
        if (!joined)
        {
            ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceNotViewable);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, room.Key);
        ApiTelemetry.RecordPresenceJoin(ApiTelemetry.PresenceJoined);

        await Clients.Group(room.Key).SendAsync("ViewersChanged", ToPublicViews(room.Key));
    }

    /// <summary>
    /// The per-type gate. Each branch answers one question and nothing else, and the
    /// method returns a bare bool on purpose: the caller must not be able to tell WHY a
    /// room was refused, because for a space room that reason is the leak (§6.7).
    /// </summary>
    private async Task<bool> IsRoomJoinableAsync(PresenceRoom room, Principal principal) => room switch
    {
        // Exactly what JoinPage did: ValueOrNull, because presence needs only
        // "viewable or not" and a denial here is not separately audited.
        PresenceRoom.Page page =>
            (await pageReadService.GetPageAsync(page.PageId, principal, Context.ConnectionAborted)).ValueOrNull() is not null,

        // Found vs NotFound vs Denied collapses to one bool HERE, at the boundary,
        // which is what makes an invisible space indistinguishable from an absent one.
        // SpaceReads returns the three-way split because its GraphQL callers must audit
        // the denial; presence audits nothing, so it takes the collapse and no more.
        PresenceRoom.Space space =>
            await SpaceReads.GetViewableSpaceByKeyAsync(db, principal, space.SpaceKey, Context.ConnectionAborted)
                is SpaceReadResult.Found,

        // Authenticated is the whole gate, and it is already satisfied: PrincipalBuilder
        // produced a principal above, and the hub itself requires authorization. The
        // route was validated against the allowlist during parsing, so there is no
        // resource here to check — that is the property that makes this safe, not an
        // absence of checking.
        PresenceRoom.Site => true,

        // No default that admits. A room type added without a gate fails closed.
        _ => false,
    };

    /// <summary>
    /// design.md §8: "leaving a page is a route change, not an unmount" — the client
    /// calls this explicitly on every in-app navigation away, rather than relying on
    /// <see cref="OnDisconnectedAsync"/> (which only fires when the connection itself
    /// closes, not on a route change that keeps the same connection alive).
    ///
    /// <para>Unauthorized here is meaningless: leaving a room you are not in is a no-op,
    /// and refusing to let someone leave would be the wrong failure. Parsing still
    /// applies, so a malformed key does nothing.</para>
    /// </summary>
    [NoAudit("Presence is deliberately unaudited (design.md §8) - see JoinRoom.")]
    public async Task LeaveRoom(string roomKey)
    {
        if (!PresenceRoom.TryParse(roomKey, out var room))
        {
            return;
        }

        registry.LeaveRoom(room.Key, Context.ConnectionId);
        ApiTelemetry.RecordPresenceLeave(ApiTelemetry.PresenceLeaveExplicit);
        await Clients.Group(room.Key).SendAsync("ViewersChanged", ToPublicViews(room.Key));
    }

    /// <summary>
    /// Broadcasts a cursor to the room. <b>Membership is the authorization</b>: the
    /// pointer is attributed from the registry's own record of this connection's
    /// presence, so a caller who never joined (or was evicted by a rule change) has
    /// nothing to attribute and is silently dropped. That is why this does not re-check
    /// canView — it cannot broadcast under an identity the registry does not already hold.
    /// </summary>
    [NoAudit("Ephemeral presence broadcast, never persisted or audited (design.md §8) - see JoinRoom.")]
    public Task PointerMove(string roomKey, double x, double y)
    {
        if (!PresenceRoom.TryParse(roomKey, out var room))
        {
            return Task.CompletedTask;
        }

        var viewer = registry.GetViewers(room.Key).FirstOrDefault(v => v.ConnectionId == Context.ConnectionId);
        if (viewer is null)
        {
            // Never joined this room (or already evicted) - nothing to attribute the
            // pointer to, and broadcasting under no identity would be exactly the
            // "content, never attributes, never more than display name/colour" leak
            // design.md §8 warns against in the other direction.
            return Task.CompletedTask;
        }

        return Clients.OthersInGroup(room.Key).SendAsync("PointerMoved", new
        {
            userId = viewer.UserId,
            displayName = viewer.DisplayName,
            colour = viewer.Colour,
            x,
            y,
        });
    }

    // --- Transitional page-shaped adapters -------------------------------------------
    //
    // The SPA still calls these while it moves to the room API, so they stay working and
    // stay THIN: each builds the page room key and defers, so there is exactly one
    // implementation of joining and one gate. They are scheduled for removal once the
    // frontend has switched — deleting them must not require re-reading any logic,
    // because there is none here to lose.

    /// <inheritdoc cref="JoinRoom"/>
    [NoAudit("Presence is deliberately unaudited (design.md §8) - see JoinRoom.")]
    public Task JoinPage(Guid pageId) => JoinRoom(new PresenceRoom.Page(pageId).Key);

    /// <inheritdoc cref="LeaveRoom"/>
    [NoAudit("Presence is deliberately unaudited (design.md §8) - see JoinRoom.")]
    public Task LeavePage(Guid pageId) => LeaveRoom(new PresenceRoom.Page(pageId).Key);

    // PointerMove has NO page-shaped adapter, because SignalR does not support
    // overloading — two methods of one name is a startup exception, not a warning.
    // The old call still works anyway: a SignalR client sends a GUID argument as a
    // JSON string, so PointerMove(pageId, x, y) arrives here as the string form, and
    // PresenceRoom.TryParse accepts a bare GUID as a page room precisely so that call
    // keeps landing on the right room during the transition.

    private object[] ToPublicViews(string roomKey) => registry.GetViewers(roomKey)
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
