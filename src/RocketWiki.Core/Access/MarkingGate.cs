namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.2: <b>the one composition</b> of every gate a protective marking imposes
/// on <c>canView</c>, in reporting order — marking availability, selector grant (G),
/// national caveat (N). Every marking check in the product goes through here: the
/// effective-permission calculator, the tree walk, the self-lockout rule in
/// <c>setPageMarking</c>, the inspector's ancestor-title withholding, page entries, and
/// notification fan-out. There is deliberately no second entry point that checks a
/// marking with fewer gates, and a source-level test pins that <see cref="CaveatGate.Check"/>
/// and <see cref="SelectorGate.Check"/> are not called from the data layer at all: the way
/// a marking gate gets forgotten is by one caller composing its own subset.
///
/// <para><b>The level is not in this list.</b> It used to be first (C: clearance against
/// the level), and it is gone because this deployment carries no clearance attribute to
/// compare it against; the level is presentational now, like the prefix, and the gate
/// reads neither (§21.12). What replaced it at the front is not a gate on the marking's
/// content but on its <i>existence</i>: <see cref="ProtectiveMarking.IsUnavailable"/>,
/// true only for the missing-row sentinel, denies before anything else is looked at.
/// It has to be a gate of its own precisely because the level stopped being one — the
/// sentinel used to deny by being TOP SECRET, and a sentinel with no selectors, no caveat
/// and a level nobody compares would otherwise deny nobody.</para>
///
/// <para>It can only subtract. Every gate is a conjunct; the order decides which reason a
/// denial names (the coarser fact first — a caller who lacks a selector is not told what
/// caveat they also fail, §7), and can change no verdict. Neither the level nor the
/// prefix is read by any of the three.</para>
///
/// <para><paramref name="grantedSelectors"/> is the caller's <see cref="SpaceAccess.GrantedSelectors"/>
/// for the page's space — the union over the access grants they match. A caller who has
/// no space access at all never reaches this gate on the enforcement path (S is
/// reported first), but the inspector evaluates it anyway with an empty set so every
/// failing gate is listed; the API decides what a caller without space access may be
/// told (§21.8).</para>
/// </summary>
public static class MarkingGate
{
    /// <summary>
    /// The reason an unavailable marking denies with. A bounded constant, like the
    /// caveat's: there is nothing about the page to name, and the audit row's job is to
    /// say "this page has no marking row" so somebody puts one back.
    /// </summary>
    public const string UnavailableReason = "marking:unavailable";

    /// <summary>
    /// The composition itself, stated once: availability, then every selector's G, then
    /// N. With <paramref name="shortCircuit"/> the list ends at the first failure (the
    /// enforcement form); without it every gate is evaluated and listed (the inspector
    /// form, and a denied tree node's complete reason list). The two public wrappers
    /// below exist so callers say which form they mean.
    ///
    /// <para>An unavailable marking is the one case where both forms return a single
    /// entry: there is no marking to evaluate selectors or a caveat against, so listing
    /// gates that passed vacuously would tell an inspector the opposite of the truth.</para>
    /// </summary>
    public static IReadOnlyList<GateCheck> Evaluate(
        ProtectiveMarking marking,
        Principal principal,
        SelectorCatalog catalog,
        IReadOnlySet<SelectorValue> grantedSelectors,
        bool shortCircuit)
    {
        if (marking.IsUnavailable)
        {
            return [GateCheck.Fail(GateKind.MarkingUnavailable, UnavailableReason)];
        }

        var checks = new List<GateCheck>(SelectorGate.Evaluate(marking, catalog, grantedSelectors, shortCircuit));
        if (shortCircuit && checks.Any(s => !s.Passed))
        {
            return checks;
        }

        checks.Add(GateCheck.From(GateKind.NationalCaveat, CaveatGate.Check(marking, principal)));
        return checks;
    }

    /// <summary>The enforcement form: the first failing gate's reason in availability, G, N order.</summary>
    public static PermissionCheckResult Check(
        ProtectiveMarking marking, Principal principal, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        GateCheck.ResultOf(Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit: true));

    /// <summary>The inspector form: every gate evaluated and listed, each selector's G
    /// then N (or the single availability failure). The first failed entry's reason is
    /// exactly what <see cref="Check"/> returns — pinned by test, so the explanation can
    /// never disagree with the gate.</summary>
    public static IReadOnlyList<GateCheck> CheckAll(
        ProtectiveMarking marking, Principal principal, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit: false);
}
