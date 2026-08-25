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
    /// exist, and the order it would be duplicating is the access comparison itself. One
    /// small query is cheaper than that, permanently.</para>
    ///
    /// <para><b>The list order IS the scheme order</b>, least sensitive first, so a client
    /// never needs to know that OFFICIAL sorts below SECRET — it renders the list as
    /// given. A numeric rank field was considered and left out: the only use for one is
    /// comparing two levels client-side, and the comparisons that matter (may I read
    /// this, may I set this) are decisions the server already makes and returns typed
    /// errors for. A rank would be an invitation to re-derive them in the browser.</para>
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
}

/// <summary>
/// One rung of the classification ladder: the enum value a mutation takes, beside the UK
/// written form a human reads. <c>Name</c> is <c>ProtectiveMarking.LevelName</c> — the
/// same method <c>PageMarkingView.levelName</c> and the composed <c>label</c> both use,
/// so there is exactly one place any level's display spelling comes from (§21.1).
/// </summary>
public sealed record ClassificationLevelInfo(ClassificationLevel Level, string Name);
