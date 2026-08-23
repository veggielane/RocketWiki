using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RocketWiki.Api.Telemetry;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// See <see cref="IEditSessionRegistry"/> for the contract and the relay-only design
/// note. All per-session state mutates under that session's own lock — seeder
/// designation, log append + cap accounting, contributor sequencing, and reseed
/// pending-ness are invariants, not approximations, which is why this is a separate
/// class from the deliberately-loose presence registry rather than more dictionaries
/// bolted onto it. The session map itself is a ConcurrentDictionary; a Removed flag
/// closes the create/sweep race (a session swept between GetOrAdd and lock acquisition
/// is detected and re-created rather than mutated as a zombie).
/// </summary>
public sealed class EditSessionRegistry(IOptions<CoEditOptions> options, TimeProvider timeProvider) : IEditSessionRegistry
{
    private readonly CoEditOptions _options = options.Value;
    private readonly ConcurrentDictionary<Guid, EditSession> _sessions = new();

    private sealed class EditSession
    {
        public readonly object Lock = new();
        public bool Removed;
        public int BaseRevisionNumber;
        public string? SeederConnectionId;
        public string? PendingReseedConnectionId;
        public readonly List<byte[]> UpdateLog = [];
        public long LogBytes;
        public readonly Dictionary<string, EditSessionMember> Members = new();
        public readonly List<string> JoinOrder = [];
        public readonly Dictionary<Guid, long> ContributorSeqByUserId = new();
        public long LastContributionSeq;
        public DateTime? EmptySinceUtc;
    }

    public EditSessionJoinOutcome Join(Guid pageId, EditSessionMember member, int currentRevisionNumber)
    {
        SweepExpired(timeProvider.GetUtcNow().UtcDateTime);

        while (true)
        {
            var created = false;
            var session = _sessions.GetOrAdd(pageId, _ =>
            {
                created = true;
                return new EditSession();
            });

            lock (session.Lock)
            {
                if (session.Removed)
                {
                    continue; // swept between GetOrAdd and lock - retry against a fresh one
                }

                if (created)
                {
                    session.BaseRevisionNumber = currentRevisionNumber;
                }

                session.EmptySinceUtc = null;

                if (session.Members.TryGetValue(member.ConnectionId, out _))
                {
                    // Idempotent re-join (e.g. the SPA retrying): report the current facts.
                    return new EditSessionJoinOutcome(
                        session.SeederConnectionId == member.ConnectionId, session.BaseRevisionNumber, SessionCreated: false);
                }

                session.Members[member.ConnectionId] = member;
                session.JoinOrder.Add(member.ConnectionId);

                // Seeder designation is the server's decision, made here under the lock
                // (design.md §8 co-editing): the first member of a fresh session, or the
                // first joiner of a session whose log is still empty and whose designated
                // seeder is gone. Once anything is logged, the log itself is the seed and
                // no seeder is needed again (late joiners replay it).
                var isSeeder = false;
                if (session.UpdateLog.Count == 0 &&
                    (session.SeederConnectionId is null || !session.Members.ContainsKey(session.SeederConnectionId)))
                {
                    session.SeederConnectionId = member.ConnectionId;
                    isSeeder = true;
                }

                return new EditSessionJoinOutcome(isSeeder, session.BaseRevisionNumber, created);
            }
        }
    }

    public IReadOnlyList<byte[]> GetLogSnapshot(Guid pageId)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return [];
        }

        lock (session.Lock)
        {
            return session.UpdateLog.ToArray();
        }
    }

    public EditSessionMember? GetMember(Guid pageId, string connectionId)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return null;
        }

        lock (session.Lock)
        {
            return session.Members.GetValueOrDefault(connectionId);
        }
    }

    public EditAppendOutcome AppendUpdate(Guid pageId, string connectionId, byte[] update)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return new EditAppendOutcome(false, null, null);
        }

        lock (session.Lock)
        {
            if (session.Removed || !session.Members.TryGetValue(connectionId, out var member))
            {
                return new EditAppendOutcome(false, null, null);
            }

            session.UpdateLog.Add(update);
            session.LogBytes += update.Length;
            session.LastContributionSeq++;
            session.ContributorSeqByUserId[member.UserId] = session.LastContributionSeq;

            ReseedDemand? demand = null;
            if (session.LogBytes > _options.LogCapBytes && session.PendingReseedConnectionId is null)
            {
                var designated = DesignateForReseedLocked(session);
                if (designated is not null)
                {
                    session.PendingReseedConnectionId = designated;
                    demand = new ReseedDemand(designated, session.BaseRevisionNumber, ReseedDemand.ReasonLogCap);
                }
            }

            return new EditAppendOutcome(true, member, demand);
        }
    }

    public bool ApplyReseed(Guid pageId, string connectionId, byte[] fullState, int pageCurrentRevisionNumber)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return false;
        }

        lock (session.Lock)
        {
            if (session.Removed || session.PendingReseedConnectionId != connectionId)
            {
                return false;
            }

            session.UpdateLog.Clear();
            session.UpdateLog.Add(fullState);
            session.LogBytes = fullState.Length;
            session.BaseRevisionNumber = pageCurrentRevisionNumber;
            session.PendingReseedConnectionId = null;
            return true;
        }
    }

    public EditSessionDeparture? Leave(Guid pageId, string connectionId)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return null;
        }

        lock (session.Lock)
        {
            return session.Removed ? null : LeaveLocked(pageId, session, connectionId);
        }
    }

    public IReadOnlyList<EditSessionDeparture> RemoveConnection(string connectionId)
    {
        var departures = new List<EditSessionDeparture>();
        foreach (var (pageId, session) in _sessions)
        {
            lock (session.Lock)
            {
                if (session.Removed)
                {
                    continue;
                }

                var departure = LeaveLocked(pageId, session, connectionId);
                if (departure is not null)
                {
                    departures.Add(departure);
                }
            }
        }

        return departures;
    }

    public IReadOnlyList<(Guid PageId, EditSessionMember Member)> GetAllMembers()
    {
        var result = new List<(Guid, EditSessionMember)>();
        foreach (var (pageId, session) in _sessions)
        {
            lock (session.Lock)
            {
                if (session.Removed)
                {
                    continue;
                }

                foreach (var member in session.Members.Values)
                {
                    result.Add((pageId, member));
                }
            }
        }

        return result;
    }

    public ContributorSnapshot? SnapshotContributorsForSave(Guid pageId, Guid actingUserId)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return null;
        }

        lock (session.Lock)
        {
            if (session.Removed || !session.Members.Values.Any(m => m.UserId == actingUserId))
            {
                return null;
            }

            return new ContributorSnapshot(session.ContributorSeqByUserId.Keys.ToArray(), session.LastContributionSeq);
        }
    }

    public void OnSaved(Guid pageId, long upToSequence, int newRevisionNumber)
    {
        if (!_sessions.TryGetValue(pageId, out var session))
        {
            return;
        }

        lock (session.Lock)
        {
            if (session.Removed)
            {
                return;
            }

            // Drain by sequence rather than clearing: a contribution that landed while
            // the save was in flight has a sequence above the snapshot's high-water
            // mark and stays marked, so the NEXT save still credits it - clearing
            // wholesale would silently drop that user from the next revision's
            // attribution (design.md §7: attribution is a record, not a best effort).
            var drained = session.ContributorSeqByUserId
                .Where(kvp => kvp.Value <= upToSequence)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var userId in drained)
            {
                session.ContributorSeqByUserId.Remove(userId);
            }

            session.BaseRevisionNumber = newRevisionNumber;
        }
    }

    public void SweepExpired(DateTime utcNow)
    {
        foreach (var (pageId, session) in _sessions)
        {
            lock (session.Lock)
            {
                if (session.Removed || session.Members.Count > 0 || session.EmptySinceUtc is null)
                {
                    continue;
                }

                if (utcNow - session.EmptySinceUtc.Value < _options.EmptySessionGrace)
                {
                    continue;
                }

                session.Removed = true;
                _sessions.TryRemove(pageId, out _);
                ApiTelemetry.RecordCoEditLogReset(ApiTelemetry.CoEditLogResetExpired);
            }
        }
    }

    private EditSessionDeparture? LeaveLocked(Guid pageId, EditSession session, string connectionId)
    {
        if (!session.Members.Remove(connectionId, out var member))
        {
            return null;
        }

        session.JoinOrder.Remove(connectionId);

        if (session.Members.Count == 0)
        {
            // Not dropped immediately: the grace window lets a refresh/reconnect find
            // the session (and its log) still alive. SweepExpired does the drop.
            session.SeederConnectionId = null;
            session.PendingReseedConnectionId = null;
            session.EmptySinceUtc = timeProvider.GetUtcNow().UtcDateTime;
            return new EditSessionDeparture(pageId, member, null);
        }

        ReseedDemand? demand = null;

        // Re-designations, in priority order. A pending cap-reseed whose designee left
        // moves to another member; a seeder lost before anything was logged is replaced
        // so the session can still be seeded at all.
        if (session.PendingReseedConnectionId == connectionId)
        {
            var designated = DesignateForReseedLocked(session);
            session.PendingReseedConnectionId = designated;
            if (designated is not null)
            {
                demand = new ReseedDemand(designated, session.BaseRevisionNumber, ReseedDemand.ReasonLogCap);
            }
        }
        else if (session.SeederConnectionId == connectionId && session.UpdateLog.Count == 0)
        {
            var newSeeder = session.JoinOrder.First();
            session.SeederConnectionId = newSeeder;
            demand = new ReseedDemand(newSeeder, session.BaseRevisionNumber, ReseedDemand.ReasonSeederLost);
        }

        return new EditSessionDeparture(pageId, member, demand);
    }

    /// <summary>Prefer the original seeder if still present, else the longest-standing
    /// member. Callers hold the session lock.</summary>
    private static string? DesignateForReseedLocked(EditSession session)
    {
        if (session.SeederConnectionId is not null && session.Members.ContainsKey(session.SeederConnectionId))
        {
            return session.SeederConnectionId;
        }

        return session.JoinOrder.Count > 0 ? session.JoinOrder.First() : null;
    }
}
