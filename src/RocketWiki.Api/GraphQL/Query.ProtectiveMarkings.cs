using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// The classification ladder itself (design.md §21.1): every level in the scheme,
    /// in scheme order, with its UK written form.
    ///
    /// <para><b>Why this exists rather than a hard-coded array in the client.</b>
    /// <c>PageMarkingView.levelName</c> answers "what is THIS page's level called", which
    /// covers a badge on a marking a caller already holds. A marking <i>picker</i> is the
    /// other half and it has no marking in hand: it must offer all four options before
    /// one is chosen. Without this field the SPA would hard-code four display spellings
    /// and their order — which is precisely the second implementation §21.1 says must not
    /// exist, and the order it would be duplicating is the one §21.13's aggregate label
    /// takes its maximum by. One small query is cheaper than that, permanently.</para>
    ///
    /// <para><b>The list order IS the scheme order</b>, least sensitive first, so a client
    /// never needs to know that OFFICIAL sorts below SECRET — it renders the list as
    /// given. A numeric rank field was considered and left out: the only use for one is
    /// comparing two levels client-side, and the level is not compared against anything
    /// about the caller — it is presentational (§21.12), so the picker offers all four to
    /// everyone. A rank would be an invitation to invent a comparison the server does not
    /// make.</para>
    ///
    /// <para>Discloses nothing: these four names are compile-time constants and the enum
    /// is already in the published SDL. Anonymous callers get an empty list anyway — the
    /// same absent-shaped answer every other read gives them, so no surface behaves
    /// differently for being unauthenticated.</para>
    /// </summary>
    [NoAudit("Compile-time vocabulary (the fixed classification ladder and its spellings), " +
        "identical for every caller and already visible in the published SDL as the ClassificationLevel enum; " +
        "no wiki content, no page, and no per-subject access decision to record - the same reasoning that " +
        "leaves the customEmojis and pagePropertyKeys registry listings unaudited (design.md §7/§21).")]
    public IReadOnlyList<ClassificationLevelInfo> ClassificationScheme(
        [Service] ICurrentPrincipalAccessor principalAccessor)
    {
        if (principalAccessor.Current is null)
        {
            return [];
        }

        // Enum.GetValues returns members in underlying-value order, and §21.1 makes those
        // values the ordering - so "declaration order" and "scheme order" are the same
        // fact here, not a coincidence this method has to maintain separately.
        return Enum.GetValues<ClassificationLevel>()
            .Select(level => new ClassificationLevelInfo(level, ProtectiveMarking.LevelName(level)))
            .ToList();
    }

    /// <summary>
    /// The instance's selector vocabulary (design.md §21.15): every configured category
    /// with its values, in configured order — the other half of the marking picker's
    /// data source beside <see cref="ClassificationScheme"/>, and the grant editor's.
    /// Identical for every authenticated caller; <c>[]</c> for anonymous, like every
    /// other read.
    ///
    /// <para><b>Whether the CALLER may pick a value is a different question</b>, with one
    /// answer, and it is not here: <c>Space.viewerSelectorGrants</c> — the values the
    /// space's access grants confer on them. There used to be a second answer
    /// (<c>me.selectorEligibility</c>, driven by a per-category Keycloak claim) and a
    /// <c>requiresAttribute</c> flag on this type saying whether a category had one;
    /// both went with the eligibility gate, because this deployment carries no
    /// per-category attributes in Keycloak. A category is now a name, a description and
    /// its values, and the space's grant is the whole of who may use it.</para>
    /// </summary>
    [NoAudit("Deployment configuration (the configured selector categories and values, design.md §21.15), " +
        "identical for every authenticated caller; no wiki content, no page, and no per-subject access decision " +
        "to record - the same reasoning as classificationScheme (design.md §7/§21).")]
    public IReadOnlyList<SelectorCategoryView> SelectorCategories(
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] SelectorCatalog catalog)
    {
        if (principalAccessor.Current is null)
        {
            return [];
        }

        return catalog.Categories
            .Select(c => new SelectorCategoryView(c.Name, c.Description, c.Values))
            .ToList();
    }
}

/// <summary>
/// One rung of the classification ladder: the enum value a mutation takes, beside the UK
/// written form a human reads. <c>Name</c> is <c>ProtectiveMarking.LevelName</c> — the
/// same method <c>PageMarkingView.levelName</c> and the composed <c>label</c> both use,
/// so there is exactly one place any level's display spelling comes from (§21.1).
/// </summary>
public sealed record ClassificationLevelInfo(ClassificationLevel Level, string Name);

/// <summary>
/// One configured selector category (design.md §21.15) as a client sees it. A view
/// record rather than Core's <c>SelectorCategory</c>, so the wire shape is decided here
/// and not by whatever Core's record grows; today the two carry the same three members.
/// It used to carry <c>requiresAttribute</c> as well — whether a Keycloak claim gated
/// eligibility for the category — which went with that gate.
/// </summary>
[GraphQLName("SelectorCategory")]
public sealed record SelectorCategoryView(
    string Name,
    string? Description,
    IReadOnlyList<string> Values);
