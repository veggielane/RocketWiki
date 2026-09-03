using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;

namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.7 / §21.8: what a principal may be told about a page they cannot view —
/// the denial as a <i>disclosure</i>, distinct from <see cref="ReadResult{T}.Denied"/>,
/// which exists for the audit row and dies at the response edge. A denied page is shown
/// as a placeholder carrying its marking and every failing gate, so the reader learns
/// what they lack rather than that nothing is there; a page whose SPACE the principal
/// cannot enter discloses nothing beyond that fact.
///
/// <para><b>The withholding rule lives here, not in the API.</b> When
/// <see cref="NoSpaceAccess"/> is true the marking is null and <see cref="Reasons"/> holds
/// the single space-access gate — never the classification, selector or caveat gates
/// the inspector still evaluated, because their tokens embed the level and the category
/// names (§21.8). Building the record this way, rather than handing the API every gate
/// and trusting it to filter, means there is no projection that can forget to.</para>
/// </summary>
/// <param name="Reason">The audit-row token the enforcement gate produced — the first
/// failing gate in ladder order (§6.4/§7).</param>
/// <param name="NoSpaceAccess">S failed: no access grant admits the principal to the
/// space. Roles never supersede access, so a Space-admin without a grant lands here too.</param>
/// <param name="Marking">The page's own marking, for the placeholder's label — null
/// exactly when <see cref="NoSpaceAccess"/>.</param>
/// <param name="Reasons">Every failing view gate in ladder order, structured
/// (<see cref="GateCheck"/>); the single S entry when <see cref="NoSpaceAccess"/>. The
/// complete internal record: the API projects it once and decides which members travel.</param>
public sealed record PageDenial(
    string Reason,
    bool NoSpaceAccess,
    ProtectiveMarking? Marking,
    IReadOnlyList<GateCheck> Reasons)
{
    /// <summary>
    /// A denial from the inspector form of the ladder — the full explanation — applying
    /// the withholding rule above. Only meaningful for an explanation whose verdict
    /// denies view; a viewable page has no denial to build.
    /// </summary>
    public static PageDenial From(EffectivePermissionExplanation explanation, ProtectiveMarking marking)
    {
        if (explanation.Permission.CanView)
        {
            throw new ArgumentException("A denial cannot be built from a permission that allows view.", nameof(explanation));
        }

        if (explanation.MarkingWithheld)
        {
            var spaceGate = explanation.ViewGates.First(g => g.Kind == GateKind.SpaceAccess && !g.Passed);
            return new PageDenial(spaceGate.Reason!, NoSpaceAccess: true, Marking: null, [spaceGate]);
        }

        return AfterSpaceAccess(explanation.ViewGates.Where(g => !g.Passed).ToList(), marking);
    }

    /// <summary>
    /// A denial for a page whose space the principal has already entered — the tree
    /// walk's case, where S was decided once for the whole space (§21.9). The marking
    /// travels, and every failed gate is listed.
    /// </summary>
    public static PageDenial AfterSpaceAccess(IReadOnlyList<GateCheck> failedGates, ProtectiveMarking marking)
    {
        if (failedGates.Count == 0 || failedGates.Any(g => g.Passed))
        {
            throw new ArgumentException("A denial needs at least one failed gate and no passed ones.", nameof(failedGates));
        }

        return new PageDenial(failedGates[0].Reason!, NoSpaceAccess: false, marking, failedGates);
    }
}

/// <summary>
/// design.md §6.7: the disclosed form of a page read. Unlike <see cref="ReadResult{T}"/>,
/// whose Denied case exists so the audit log can record a refusal before the response
/// collapses it to null, a <see cref="Denied"/> here is meant to REACH the principal — as
/// a placeholder with the page's marking and the gates they failed (§21.8). Three cases,
/// exactly one of which applies:
/// <list type="bullet">
/// <item><see cref="NotFound"/> — no such live page; nothing to disclose and nothing to audit.</item>
/// <item><see cref="Found"/> — canView passed; the page itself.</item>
/// <item><see cref="Denied"/> — the page exists and canView failed; the denial, built
/// from the full ladder with the withholding rule applied (<see cref="PageDenial"/>).</item>
/// </list>
/// The plain read (<c>GetPageAsync</c>) is unchanged and stays leak-free: this is a second
/// contract for the surfaces that disclose, not a relaxation of the first.
/// </summary>
public abstract record PageAccess
{
    private PageAccess()
    {
    }

    /// <summary>No such live page exists. No access decision was made.</summary>
    public sealed record NotFound : PageAccess;

    /// <summary>The principal can view the page.</summary>
    public sealed record Found(Page Page) : PageAccess;

    /// <summary>The page exists and the principal cannot view it; what they may be told is in <see cref="Denial"/>.</summary>
    public sealed record Denied(PageDenial Denial) : PageAccess;
}
