using System.Text.Json;
using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// One person's profile page (design.md §6.2): the three <see cref="UserRef"/> facts,
/// whether the account is a sync shadow, and the group memberships <b>recorded at their
/// last request</b>. A view record with an explicit GraphQL name, like
/// <c>SelectorCategoryView</c>, so the schema type is <c>UserProfile</c> without a C#
/// type shadowing the <c>Query.UserProfile</c> resolver.
///
/// <para><b>The shape changed, by product decision.</b> The page used to show a clearance
/// and a per-category selector eligibility, both re-derived through the gates from the
/// mirrored claims. Those gates are gone — this deployment carries neither a clearance
/// nor per-category selector attributes in Keycloak — so there is nothing to derive and
/// nothing to show; what a colleague can usefully see about a person now is which groups
/// they were in at their last sign-in, which is what every access grant and restriction
/// is written against (§6.3). Groups are recorded raw and shown raw: there is no gate to
/// re-run over them, so the profile reads the mirrored list and nothing interprets it.</para>
///
/// <para><b>The fields are the whole list, on purpose.</b> Nationality, email and
/// last-seen are not here and must not be added without the decision §6.2 records for
/// the one that is: nationality is sensitive personal data (admin-only, §6.2), and email
/// and last-seen together make a surveillance surface (§15's reasoning, the same one
/// that keeps them off <c>userDirectory</c>). No timestamp of the recording either — the
/// page says "as of your last sign-in" in words; a time would be a last-seen field by
/// another name.</para>
/// </summary>
/// <param name="Groups">The group memberships the subject's token carried at their last
/// request, in ordinal order. Empty for a shadow account (nothing was ever recorded) and
/// for a person whose token carried no groups.</param>
[GraphQLName("UserProfile")]
public sealed record UserProfileView(
    Guid Id,
    string DisplayName,
    bool HasAvatar,
    [property: GraphQLDescription(
        "A shadow account created by sync; it has never signed in here, so nothing below was recorded.")]
    bool IsExternal,
    [property: GraphQLDescription(
        "The group memberships recorded at this user's last sign-in, in ordinal order - the same groups claim access rules are written against.")]
    IReadOnlyList<string> Groups);

public partial class Query
{
    /// <summary>
    /// The profile page: any authenticated caller may read any user's group memberships
    /// as recorded at that user's last request.
    ///
    /// <para><b>This is a widening, and it is deliberate.</b> <c>userDirectory</c>'s doc
    /// refuses to carry any rule-engine attribute because a directory of them is a
    /// who-holds-what census; this field is precisely that census, one person at a time,
    /// and it exists because the product owner decided (design.md §6.2) that colleagues
    /// seeing each other's group memberships is worth more than denying an insider that
    /// map. The directory itself is unchanged — still <c>UserRef</c> only — so
    /// enumerating the census still costs one request per person and an id from
    /// somewhere; nationality, email and last-seen remain admin-only on <c>users</c>.
    /// Anyone extending this type should read §6.2's trade-off paragraph first, because
    /// that is the decision being extended.</para>
    ///
    /// <para><b>Read straight from the mirror.</b> The row's <c>AttributesJson</c> holds
    /// the <c>groups</c> claim as JIT provisioning recorded it
    /// (<see cref="JitUserProvisioningMiddleware"/>); this reads that list back, sorts it
    /// ordinally, drops duplicates, and interprets nothing. There used to be a rebuilt principal here so
    /// the clearance and eligibility gates could run over the recorded claims; with those
    /// gates gone there is no rule to keep a single implementation of, and a mirror that
    /// cannot be parsed simply reads as no groups.</para>
    ///
    /// <para><b>One row, projected in the database</b>: exactly this user's
    /// <c>AttributesJson</c> is fetched, never a list of them (the directory keeps that
    /// column out of its query entirely, and this field is the only non-admin reader of
    /// it). Unknown id and anonymous caller both return null, the convention <c>page</c>
    /// uses; there is no existence to protect from a signed-in user, but a profile is
    /// not a thing an anonymous request gets to ask about.</para>
    ///
    /// <para><b>Not an authorization read.</b> Nothing here decides anything; the mirror
    /// stays what §6.1 says it is. The gates on every content path still evaluate the
    /// token.</para>
    /// </summary>
    [NoAudit("Reads one user's display-safe facts plus the group memberships recorded at their last sign-in - no wiki content, no page, and no per-subject access decision, the same reasoning that leaves userDirectory, display-name resolution and the emoji vocabulary unaudited (design.md §7). What it discloses, stated plainly: group membership is shown to every signed-in user by product decision (design.md §6.2) - precisely the who-holds-what census userDirectory's doc refused for the directory. The directory itself is unchanged (UserRef only); nationality, email and last-seen remain admin-only on `users`, which IS audited.")]
    public async Task<UserProfileView?> UserProfile(
        Guid id,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] RocketWikiDbContext db,
        CancellationToken cancellationToken)
    {
        if (principalAccessor.Current is null)
        {
            return null;
        }

        // HasAvatar is a correlated EXISTS in the same single-row query, the shape
        // UserRefByIdDataLoader and userDirectory use; provider-agnostic LINQ (§14).
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new
            {
                u.Id,
                u.DisplayName,
                u.IsExternal,
                u.AttributesJson,
                HasAvatar = db.UserAvatars.Any(a => a.UserId == u.Id),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new UserProfileView(
            row.Id,
            row.DisplayName,
            row.HasAvatar,
            row.IsExternal,
            MirroredGroups(row.AttributesJson));
    }

    /// <summary>
    /// The <c>groups</c> list out of a mirror, ordinal-sorted for a stable page. Malformed
    /// JSON, a missing key, a non-list value or null elements (none of which JIT
    /// provisioning writes; all of which a hand edit could) read as no groups rather than
    /// as an error — a profile read must not turn into a 500 over a mirror row, and
    /// "nothing recorded" is the honest reading of a mirror that cannot be read.
    /// </summary>
    private static IReadOnlyList<string> MirroredGroups(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return [];
        }

        try
        {
            var mirror = JsonSerializer.Deserialize<Dictionary<string, string[]?>>(attributesJson);
            if (mirror is null || !mirror.TryGetValue(JitUserProvisioningMiddleware.GroupsClaimName, out var groups) || groups is null)
            {
                return [];
            }

            return groups
                .Where(g => g is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(g => g, StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
