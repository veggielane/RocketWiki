using HotChocolate;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Data;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// One person's profile page (design.md §6.2, product decision of 2026-09-03): the three
/// <see cref="UserRef"/> facts, whether the account is a sync shadow, and the clearance
/// and selector eligibility <b>recorded at their last request</b> — resolved through the
/// gates, never re-derived. A view record with an explicit GraphQL name, like
/// <c>SelectorCategoryView</c>, so the schema type is <c>UserProfile</c> without a C#
/// type shadowing the <c>Query.UserProfile</c> resolver.
///
/// <para><b>The fields are the whole list, on purpose.</b> Nationality, email and
/// last-seen are not here and must not be added without the decision §6.2 records for
/// the two that are: nationality is sensitive personal data (admin-only, §6.2), and email
/// and last-seen together make a surveillance surface (§15's reasoning, the same one
/// that keeps them off <c>userDirectory</c>). No timestamp of the recording either — the
/// page says "as of your last sign-in" in words; a time would be a last-seen field by
/// another name.</para>
/// </summary>
/// <param name="ClearanceName">The UK written form of <paramref name="Clearance"/>
/// (<c>OFFICIAL-SENSITIVE</c>, <c>TOP SECRET</c>), from the one place any level's display
/// spelling comes from (<c>ProtectiveMarking.LevelName</c>, §21.1).</param>
/// <param name="SelectorEligibility">Every configured category, in catalog (display)
/// order, each with the gate's answer for this person. Not only the eligible ones: the
/// page's job is to show a status per category, and "not eligible" is a status.</param>
[GraphQLName("UserProfile")]
public sealed record UserProfileView(
    Guid Id,
    string DisplayName,
    bool HasAvatar,
    [property: GraphQLDescription(
        "A shadow account created by sync; it has never signed in here, so nothing below was recorded.")]
    bool IsExternal,
    [property: GraphQLDescription(
        "Resolved exactly as the clearance gate resolves it from the recorded claim (absent/unrecognised => OFFICIAL_SENSITIVE).")]
    ClassificationLevel Clearance,
    string ClearanceName,
    [property: GraphQLDescription(
        "False when no recognised clearance claim was recorded - the floor above is the gate's default, not a recorded fact.")]
    bool ClearanceRecorded,
    IReadOnlyList<SelectorEligibilityStatus> SelectorEligibility);

/// <summary>
/// One configured selector category's eligibility for the profiled person (design.md
/// §21.15's E gate, rendered): <see cref="RequiresAttribute"/> mirrors
/// <c>SelectorCategory.requiresAttribute</c> so the page can say "everyone is eligible"
/// rather than "eligible" for a claim-less category, and <see cref="Eligible"/> is
/// <c>SelectorGate</c>'s own answer over the mirrored principal. The claim name is not
/// here for the reason <c>selectorCategories</c> withholds it.
/// </summary>
public sealed record SelectorEligibilityStatus(string Category, bool RequiresAttribute, bool Eligible);

public partial class Query
{
    /// <summary>
    /// The profile page: any authenticated caller may read any user's clearance and
    /// selector eligibility as recorded at that user's last request.
    ///
    /// <para><b>This is a widening, and it is deliberate.</b> <c>userDirectory</c>'s doc
    /// refuses to carry any rule-engine attribute because a directory of them is a
    /// who-holds-what-clearance census; this field is precisely that census, one person
    /// at a time, and it exists because the product owner decided (design.md §6.2,
    /// 2026-09-03) that colleagues seeing each other's clearance and compartment
    /// eligibility is worth more than denying an insider that map. The directory itself
    /// is unchanged — still <c>UserRef</c> only — so enumerating the census still costs
    /// one request per person and an id from somewhere; nationality, email and last-seen
    /// remain admin-only on <c>users</c>. Anyone extending this type should read §6.2's
    /// trade-off paragraph first, because that is the decision being extended.</para>
    ///
    /// <para><b>Resolved through the gates over a mirrored principal</b>
    /// (<see cref="MirroredPrincipal"/>): <c>ClearanceGate.ResolveClearance</c> and
    /// <c>SelectorGate.ResolveEligibleCategories</c> run unchanged over a Principal
    /// rebuilt from the row's <c>AttributesJson</c>, so the page shows what the gate
    /// would have decided from those claims — the same discipline <c>me</c> follows for
    /// the caller's own values. <c>clearanceRecorded</c> uses the gate's parser too: it
    /// is "some recorded clearance value is one the gate recognises", so the floor is
    /// reported as a floor rather than as a fact about the person.</para>
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
    [NoAudit("Reads one user's display-safe facts plus the clearance and selector eligibility recorded at their last sign-in - no wiki content, no page, and no per-subject access decision, the same reasoning that leaves userDirectory, display-name resolution and the emoji vocabulary unaudited (design.md §7). What changed, stated plainly: clearance and selector eligibility are now shown to every signed-in user by product decision (design.md §6.2, 2026-09-03) - precisely the who-holds-what census userDirectory's doc refused for the directory. The directory itself is unchanged (UserRef only); nationality, email and last-seen remain admin-only on `users`, which IS audited.")]
    public async Task<UserProfileView?> UserProfile(
        Guid id,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] RocketWikiDbContext db,
        [Service] SelectorCatalog catalog,
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
                u.Subject,
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

        var mirrored = MirroredPrincipal.Build(row.Subject ?? row.Id.ToString("D"), row.AttributesJson, catalog);

        var clearance = ClearanceGate.ResolveClearance(mirrored);
        var clearanceRecorded =
            mirrored.Attributes.TryGetValue(ClearanceGate.ClearanceAttributeKey, out var recordedClearance)
            && recordedClearance.Any(value => ClearanceGate.TryParseLevel(value, out _));

        // Catalog order, every category - the page renders a status per row, and the
        // order is the display order every picker and label uses (§21.15).
        var eligible = SelectorGate.ResolveEligibleCategories(mirrored, catalog);
        var eligibility = catalog.Categories
            .Select(c => new SelectorEligibilityStatus(c.Name, c.RequiresClaim, eligible.Contains(c.Name)))
            .ToList();

        return new UserProfileView(
            row.Id,
            row.DisplayName,
            row.HasAvatar,
            row.IsExternal,
            clearance,
            ProtectiveMarking.LevelName(clearance),
            clearanceRecorded,
            eligibility);
    }
}
