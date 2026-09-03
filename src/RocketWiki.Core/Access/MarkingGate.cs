namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.2: <b>the one composition</b> of every gate a protective marking imposes
/// on <c>canView</c>, in reporting order — classification (C), selector eligibility (E),
/// selector grant (G), national caveat (N). Every marking check in the product goes
/// through here: the effective-permission calculator, the tree walk, the self-lockout
/// rule in <c>setPageMarking</c>, the inspector's ancestor-title withholding, page
/// entries, and notification fan-out. There is deliberately no second entry point that
/// checks a marking with fewer gates, and a source-level test pins that
/// <see cref="ClearanceGate.Check"/> is not called from the data layer at all: the way a
/// new marking gate gets forgotten is by one caller composing its own subset.
///
/// <para>It can only subtract. Every gate is a conjunct; the order decides which reason a
/// denial names (the coarser fact first — a caller who lacks the level is not told what
/// selectors they also lack, §7), and can change no verdict. The prefix is not read by
/// any of the four (§21.12).</para>
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
    /// The composition itself, stated once: C, then every selector's E, then every
    /// selector's G, then N. With <paramref name="shortCircuit"/> the list ends at the
    /// first failure (the enforcement form); without it every gate is evaluated and
    /// listed (the inspector form, and a denied tree node's complete reason list). The
    /// two public wrappers below exist so callers say which form they mean.
    /// </summary>
    public static IReadOnlyList<GateCheck> Evaluate(
        ProtectiveMarking marking,
        Principal principal,
        SelectorCatalog catalog,
        IReadOnlySet<SelectorValue> grantedSelectors,
        bool shortCircuit)
    {
        var checks = new List<GateCheck>
        {
            GateCheck.From(GateKind.Classification, ClearanceGate.CheckClassification(marking, principal)),
        };
        if (shortCircuit && !checks[0].Passed)
        {
            return checks;
        }

        var selectors = SelectorGate.Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit);
        checks.AddRange(selectors);
        if (shortCircuit && selectors.Any(s => !s.Passed))
        {
            return checks;
        }

        checks.Add(GateCheck.From(GateKind.NationalCaveat, ClearanceGate.CheckCaveat(marking, principal)));
        return checks;
    }

    /// <summary>The enforcement form: the first failing gate's reason in C, E, G, N order.</summary>
    public static PermissionCheckResult Check(
        ProtectiveMarking marking, Principal principal, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        GateCheck.ResultOf(Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit: true));

    /// <summary>The inspector form: every gate evaluated and listed, C first, then each
    /// selector's E, each selector's G, then N. The first failed entry's reason is exactly
    /// what <see cref="Check"/> returns — pinned by test, so the explanation can never
    /// disagree with the gate.</summary>
    public static IReadOnlyList<GateCheck> CheckAll(
        ProtectiveMarking marking, Principal principal, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit: false);
}
