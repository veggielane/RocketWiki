using RocketWiki.Core.Access;

namespace RocketWiki.Api.RealTime;

/// <summary>One edit-session member's server-side facts. <c>Principal</c> is retained
/// for the rule-change re-authorization sweep (the same reason
/// <see cref="IRealtimeConnectionRegistry"/> retains presence principals); ClientIp is
/// captured at join so leave/eviction audit rows carry the same request context the
/// join row did.</summary>
public sealed record EditSessionMember(string ConnectionId, Guid UserId, Principal Principal, string ClientIp);

/// <summary>Result of a successful join: the caller's role, and the page revision the
/// session's authoritative saves currently build on (the expectedRevisionNumber for
/// the next updatePageContent).</summary>
public sealed record EditSessionJoinOutcome(bool IsSeeder, int BaseRevisionNumber, bool SessionCreated);

/// <summary>A server-issued demand that one member produce document state — see
/// <c>ReseedRequired</c> in the hub protocol. Reason is a bounded vocabulary:
/// <c>log_cap</c> (save, then call ReseedEditSession with a full-state snapshot) or
/// <c>seeder_lost</c> (the designated seeder left before any update was logged; seed
/// from CurrentContent at BaseRevisionNumber and push it as an ordinary update).</summary>
public sealed record ReseedDemand(string ConnectionId, int BaseRevisionNumber, string Reason)
{
    public const string ReasonLogCap = "log_cap";
    public const string ReasonSeederLost = "seeder_lost";
}

/// <summary>Outcome of appending one update: whether the caller was a member (silent
/// drop otherwise), and a reseed demand if this append newly crossed the log cap.</summary>
public sealed record EditAppendOutcome(bool Accepted, EditSessionMember? Member, ReseedDemand? Demand);

/// <summary>One member's departure from one session (leave, disconnect, or eviction),
/// with any re-designation demand it triggered.</summary>
public sealed record EditSessionDeparture(Guid PageId, EditSessionMember Member, ReseedDemand? Demand);

/// <summary>The distinct users whose updates are in the session since its last save,
/// plus the contribution sequence high-water mark the snapshot covers — pass it back
/// to <see cref="IEditSessionRegistry.OnSaved"/> so contributions that land while the
/// save is in flight are credited to the NEXT revision instead of silently dropped.</summary>
public sealed record ContributorSnapshot(IReadOnlyList<Guid> UserIds, long MaxSequence);

/// <summary>
/// design.md §8 (CRDT co-editing, promoted from §17): server-side state of live edit
/// sessions — one per page, hub-managed, in-memory only. The server RELAYS opaque Yjs
/// binary updates and retains a session-scoped update log for late joiners; it never
/// interprets CRDT state (see NotificationsHub.EditSessions for the full decision
/// note). Everything here is ephemeral exactly like presence: no table, gone on API
/// restart — the authoritative content path remains updatePageContent, and the only
/// durable artifacts of a session are its audit rows (§7) and the
/// PageRevisionContributor rows its saves record.
///
/// Unlike the presence registry (approximate, self-healing), sessions need real
/// invariants — a single designated seeder, an ordered update log, exact contributor
/// draining — so the implementation locks per session rather than leaning on
/// ConcurrentDictionary semantics alone. Singleton, keyed by SignalR ConnectionId,
/// same as <see cref="IRealtimeConnectionRegistry"/>.
/// </summary>
public interface IEditSessionRegistry
{
    /// <summary>
    /// Join (idempotent per connection). The FIRST joiner creates the session and is
    /// designated seeder — a server-side decision under the session lock, never a
    /// client claim; <paramref name="currentRevisionNumber"/> (the page's live
    /// CurrentRevisionNumber, read by the hub) becomes the session's base revision.
    /// A joiner of a session whose log is still empty and whose seeder has gone is
    /// promoted to seeder itself. Callers must have enforced canEdit BEFORE calling
    /// (design.md §6.7 — this class holds no rule knowledge).
    /// </summary>
    EditSessionJoinOutcome Join(Guid pageId, EditSessionMember member, int currentRevisionNumber);

    /// <summary>Snapshot of the retained update log, oldest first. Take it AFTER the
    /// connection is in the SignalR group: an update racing the join may then arrive
    /// both in the snapshot and as a live relay — harmless, Yjs updates are idempotent
    /// under merge — whereas the reverse order could lose one, which is not.</summary>
    IReadOnlyList<byte[]> GetLogSnapshot(Guid pageId);

    EditSessionMember? GetMember(Guid pageId, string connectionId);

    /// <summary>Append one update: marks the member as a contributor and returns a
    /// reseed demand if this append newly crossed the log cap. Not-a-member ⇒
    /// Accepted=false (silent drop; the caller counts it).</summary>
    EditAppendOutcome AppendUpdate(Guid pageId, string connectionId, byte[] update);

    /// <summary>Replace the log with one full-state snapshot (the log-cap reseed).
    /// Only honoured for the connection a pending <see cref="ReseedDemand"/> named;
    /// <paramref name="pageCurrentRevisionNumber"/> is read from the database by the
    /// hub — the client's own claim about what it saved is deliberately ignored.</summary>
    bool ApplyReseed(Guid pageId, string connectionId, byte[] fullState, int pageCurrentRevisionNumber);

    /// <summary>Explicit leave. Null if the connection wasn't a member.</summary>
    EditSessionDeparture? Leave(Guid pageId, string connectionId);

    /// <summary>Disconnect cleanup: departs every session this connection was in.</summary>
    IReadOnlyList<EditSessionDeparture> RemoveConnection(string connectionId);

    /// <summary>Every (page, member) pair — the working set the rule-change
    /// re-authorization sweep re-checks against canEdit (design.md §6.7/§8).</summary>
    IReadOnlyList<(Guid PageId, EditSessionMember Member)> GetAllMembers();

    /// <summary>
    /// The contributor set for a save of <paramref name="pageId"/> by
    /// <paramref name="actingUserId"/> — null unless a session exists AND the acting
    /// user is currently a member of it. That membership condition is the §7
    /// anti-forgery gate on the save side: a client cannot claim contributors (no
    /// input field exists), and a non-member's save — content produced outside the
    /// session — never gets the session's attribution attached.
    /// </summary>
    ContributorSnapshot? SnapshotContributorsForSave(Guid pageId, Guid actingUserId);

    /// <summary>After a successful session save: drain contributor marks up to the
    /// snapshot's sequence and advance the session's base revision to the newly
    /// created revision number.</summary>
    void OnSaved(Guid pageId, long upToSequence, int newRevisionNumber);

    /// <summary>Drop sessions that have been empty longer than the grace period
    /// (design.md §8: "log dropped after last leave + grace"). Runs opportunistically
    /// on every Join; exposed for deterministic tests.</summary>
    void SweepExpired(DateTime utcNow);
}
