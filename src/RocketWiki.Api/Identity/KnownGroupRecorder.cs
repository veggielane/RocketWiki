using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.Identity;

/// <summary>
/// design.md §6.6: "known groups are accumulated from observed logins (plus manual add),
/// avoiding a Keycloak admin-API dependency in v1." The accumulation half did not exist —
/// nothing in the source tree wrote a <see cref="KnownGroup"/> row, so the table was
/// permanently empty and the rule builder's group picker (Query.Groups) could only ever
/// offer names already used in a stored rule plus the caller's own token groups. A group
/// nobody had written a rule for yet was invisible to the admin trying to write the first
/// one, which is precisely the case the picker exists for.
///
/// <para><b>Suggestion vocabulary, never authority.</b> Query.Groups' own doc says so, and
/// it matters twice here. It is why a failure to record is swallowed rather than failing
/// the request: the rule engine accepts any string and matches it ordinally (§6.3), so a
/// missing picker suggestion costs an admin one keystroke, while a login that 500s because
/// two nodes raced to insert the same group name would be an outage. And it is why nothing
/// here is ever read back for an authorization decision — the Principal is still built
/// straight from the token (§6.1).</para>
///
/// <para><b>Cost.</b> This sits on the hottest path in the system (every authenticated
/// request), so the steady state must be free. A process-wide set of names already known
/// to exist means the database is touched only the first time this process sees a given
/// group name; after that the check is a hash lookup and no query is issued at all. The
/// set only ever grows and only ever holds names that ARE in the table, so it can go
/// stale in exactly one harmless direction — a row deleted out from under it, which would
/// merely stop being re-suggested.</para>
/// </summary>
public sealed class KnownGroupRecorder
{
    /// <summary>Ordinal, like every other group-name comparison in the system (§6.3) —
    /// and, since BinaryCollationOnStringKeys, like the unique index this guards.</summary>
    private readonly ConcurrentDictionary<string, byte> _recorded = new(StringComparer.Ordinal);

    public async Task RecordAsync(RocketWikiDbContext db, IReadOnlyCollection<string> groupNames, CancellationToken cancellationToken)
    {
        if (groupNames.Count == 0)
        {
            return;
        }

        var unseen = groupNames
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= KnownGroupNameMaxLength)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !_recorded.ContainsKey(name))
            .ToList();
        if (unseen.Count == 0)
        {
            return;
        }

        var existing = await db.KnownGroups
            .AsNoTracking()
            .Where(g => unseen.Contains(g.Name))
            .Select(g => g.Name)
            .ToListAsync(cancellationToken);
        foreach (var name in existing)
        {
            _recorded.TryAdd(name, 0);
        }

        var missing = unseen.Except(existing, StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var name in missing)
        {
            db.KnownGroups.Add(new KnownGroup
            {
                Name = name,
                Source = KnownGroupSource.ObservedAtLogin,
                FirstSeenAtUtc = now,
            });
        }

        try
        {
            // Its own save, deliberately separate from the JIT user upsert: this is
            // picker bookkeeping and must not be able to take a login's User row down
            // with it. No domain event and no audit row either, for the same reason JIT
            // provisioning raises none — §7 audits user actions, not the plumbing that
            // notices what a token said.
            await db.SaveChangesAsync(cancellationToken);
            foreach (var name in missing)
            {
                _recorded.TryAdd(name, 0);
            }
        }
        catch (DbUpdateException)
        {
            // Another request (or another node) inserted the same name first — the unique
            // index doing its job. The row exists either way, which is the only outcome
            // this method cares about, so detach the losers and carry on rather than
            // failing a login over a suggestion.
            foreach (var entry in db.ChangeTracker.Entries<KnownGroup>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>Matches KnownGroupConfiguration's HasMaxLength. Checked here because
    /// SQLite does not enforce declared lengths, so an over-long group name from a
    /// malformed token would insert silently in the test tier and throw in production —
    /// the same tier-parity reason PageMarkingService checks its prefix length.</summary>
    private const int KnownGroupNameMaxLength = 255;
}
