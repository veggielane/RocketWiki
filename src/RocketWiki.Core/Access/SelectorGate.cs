namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.15: the selector gate on <c>canView</c>. For every selector a page's
/// marking carries, the principal must be <b>granted</b> the value in this space (G) —
/// some access grant the principal matches carries that exact (category, value), so the
/// value is in the <see cref="SpaceAccess.GrantedSelectors"/> union. It is a conjunct with
/// everything else on the view path, so like <see cref="CaveatGate"/> this class can only
/// subtract: nothing here returns "allow" for a page the surrounding computation would
/// have denied.
///
/// <para><b>There used to be a second gate here — eligibility (E)</b>: a per-category
/// Keycloak claim that had to say <c>yes</c> before the space's grant was even
/// consulted. It is gone, because this deployment does not carry per-category selector
/// attributes in Keycloak: a gate that read a claim nobody emits would have made every
/// gated category eligible to nobody, which is not a control but an outage with a
/// security-shaped excuse. Selectors still gate, once, through the space's access grants
/// — G answers "has this space's administrator let them", and it is the space's decision
/// alone (§6.4, §21.15).</para>
///
/// <para><b>Fail closed, every branch.</b> A value no matching grant carries: not
/// granted. An unknown category (removed from configuration, or configured only on the
/// instance a bundle came from — §12) can never be granted — the grant writers refuse a
/// value the catalog does not know — so it already fails closed; it is reported with its
/// own token, <c>selector:unknown:{CATEGORY}</c>, so an operator can tell a missing
/// configuration from a missing grant. There is no default that admits.</para>
///
/// <para><b>Reason tokens name the category, never the value.</b> A category name is
/// bounded configured vocabulary and collapses to the single metric category
/// <c>selector</c> (§15); a value is the marking's content and travels only in the
/// structured <see cref="GateCheck"/>. Selectors are visited in the marking's canonical
/// order, so the first failing reason is stable (§7).</para>
/// </summary>
public static class SelectorGate
{
    /// <summary>The category is not configured on this instance. Suffix is the category name.</summary>
    public const string UnknownCategoryReasonPrefix = "selector:unknown:";

    /// <summary>G failed: no matching access grant carries the value. Suffix is the category name.</summary>
    public const string NotGrantedReasonPrefix = "selector:not_granted:";

    /// <summary>
    /// The selector gate stated once: every selector's G, in the marking's canonical
    /// order. With <paramref name="shortCircuit"/> the list ends at the first failure
    /// (the enforcement form); without it every check is evaluated regardless of the
    /// others — "a separate method, never a relaxation of the gate" (§6.6). Empty for a
    /// marking with no selectors.
    /// </summary>
    public static IReadOnlyList<GateCheck> Evaluate(
        ProtectiveMarking marking,
        SelectorCatalog catalog,
        IReadOnlySet<SelectorValue> grantedSelectors,
        bool shortCircuit)
    {
        if (!marking.HasSelectors)
        {
            return [];
        }

        var checks = new List<GateCheck>(marking.Selectors.Count);
        foreach (var selector in marking.Selectors)
        {
            var grant = CheckGrant(selector, catalog, grantedSelectors);
            checks.Add(grant);
            if (shortCircuit && !grant.Passed)
            {
                return checks;
            }
        }

        return checks;
    }

    /// <summary>The enforcement form: the first failing selector's reason, in canonical
    /// order, or allow when every selector is granted.</summary>
    public static PermissionCheckResult Check(
        ProtectiveMarking marking, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        GateCheck.ResultOf(Evaluate(marking, catalog, grantedSelectors, shortCircuit: true));

    /// <summary>The inspector form: every selector's G check, each evaluated regardless
    /// of the others. Empty for a marking with no selectors.</summary>
    public static IReadOnlyList<GateCheck> CheckAll(
        ProtectiveMarking marking, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        Evaluate(marking, catalog, grantedSelectors, shortCircuit: false);

    /// <summary>
    /// G for one selector. The catalog is consulted for diagnosis only: a category the
    /// instance has not configured is reported as unknown rather than as ungranted, so
    /// the audit row tells an operator "misconfiguration" rather than "ask the space
    /// admin". It cannot change the verdict — an unknown category has no value a grant
    /// could carry, so it fails either way, and there is no branch in which an
    /// unconfigured category passes.
    /// </summary>
    public static GateCheck CheckGrant(SelectorValue selector, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors)
    {
        if (!catalog.TryGet(selector.Category, out _))
        {
            return Fail(UnknownCategoryReasonPrefix, selector);
        }

        return grantedSelectors.Contains(selector)
            ? Pass(selector)
            : Fail(NotGrantedReasonPrefix, selector);
    }

    private static GateCheck Pass(SelectorValue selector) =>
        new(GateKind.SelectorGrant, true, null, selector.Category, selector.Value);

    private static GateCheck Fail(string reasonPrefix, SelectorValue selector) =>
        new(GateKind.SelectorGrant, false, reasonPrefix + selector.Category, selector.Category, selector.Value);
}
