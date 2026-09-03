namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21.15: the two selector gates on <c>canView</c>. For every selector a page's
/// marking carries, the principal must be
/// <list type="number">
/// <item><b>eligible</b> for its category (E) — the category is configured on this
/// instance and either gates nobody (<see cref="SelectorCategory.ClaimName"/> null) or
/// the principal's claim for it says <c>yes</c>; and</item>
/// <item><b>granted</b> the value in this space (G) — some access grant the principal
/// matches carries that exact (category, value), so the value is in the
/// <see cref="SpaceAccess.GrantedSelectors"/> union.</item>
/// </list>
/// Both are conjuncts with everything else on the view path, so like
/// <see cref="ClearanceGate"/> this class can only subtract: nothing here returns "allow"
/// for a page the surrounding computation would have denied.
///
/// <para><b>Fail closed, every branch.</b> An unknown category (removed from configuration,
/// or configured only on the instance a bundle came from — §12) is eligible to nobody and
/// says so with its own token, <c>selector:unknown:{CATEGORY}</c>, so an operator can
/// tell a missing configuration from a missing claim. A missing claim, a blank claim, a
/// claim saying anything but <c>yes</c>: not eligible. A value no matching grant carries:
/// not granted. There is no default that admits.</para>
///
/// <para><b>Order: all E, then all G.</b> Eligibility is a fact about the principal and
/// the instance; the grant is a fact about the space. Reporting the coarser fact first is
/// the same choice <see cref="ClearanceGate"/> makes between level and caveat, and it
/// means a principal who is not eligible for a category is never told what the space
/// would have granted them in it. Within a gate, selectors are visited in the marking's
/// canonical order, so the first failing reason is stable (§7).</para>
///
/// <para><b>Reason tokens name the category, never the value.</b> A category name is
/// bounded configured vocabulary, like a level, and collapses to the single metric
/// category <c>selector</c> (§15); a value is the marking's content and travels only in
/// the structured <see cref="GateCheck"/>.</para>
///
/// <para>The <c>yes</c> comparison is trimmed and case-insensitive — the second
/// documented departure from §6.3's ordinal rule, confined to marking comparisons for the
/// reason §21.4 gives: the claim value is set by a realm mapper this code does not
/// control, and failing closed on <c>Yes</c> versus <c>yes</c> would be an outage
/// dressed as security.</para>
/// </summary>
public static class SelectorGate
{
    /// <summary>The claim value that makes a principal eligible for a gated category.</summary>
    public const string EligibilityClaimValue = "yes";

    /// <summary>E failed: configured category, principal not eligible. Suffix is the category name.</summary>
    public const string NotEligibleReasonPrefix = "selector:not_eligible:";

    /// <summary>E failed: category not configured on this instance. Suffix is the category name.</summary>
    public const string UnknownCategoryReasonPrefix = "selector:unknown:";

    /// <summary>G failed: no matching access grant carries the value. Suffix is the category name.</summary>
    public const string NotGrantedReasonPrefix = "selector:not_granted:";

    /// <summary>
    /// The configured categories the principal is eligible for: every claim-less category,
    /// plus every gated category whose claim holds <c>yes</c>. Unknown categories are
    /// never in the result. Also what <c>me.selectorEligibility</c> renders, so the picker
    /// and the gate agree by construction.
    /// </summary>
    public static IReadOnlySet<string> ResolveEligibleCategories(Principal principal, SelectorCatalog catalog)
    {
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in catalog.Categories)
        {
            if (IsEligible(principal, category))
            {
                eligible.Add(category.Name);
            }
        }

        return eligible;
    }

    /// <summary>One category's eligibility test, stated once. A claim-less category admits
    /// everyone; otherwise some value of the named attribute must be <c>yes</c>.</summary>
    public static bool IsEligible(Principal principal, SelectorCategory category)
    {
        if (category.ClaimName is null)
        {
            return true;
        }

        if (!principal.Attributes.TryGetValue(category.ClaimName, out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null
                && string.Equals(value.Trim(), EligibilityClaimValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The two selector gates stated once: every selector's E, then every selector's G,
    /// in the marking's canonical order. With <paramref name="shortCircuit"/> the list
    /// ends at the first failure (the enforcement form); without it every check is
    /// evaluated regardless of the others — "a separate method, never a relaxation of
    /// the gate" (§6.6). Empty for a marking with no selectors.
    /// </summary>
    public static IReadOnlyList<GateCheck> Evaluate(
        ProtectiveMarking marking,
        Principal principal,
        SelectorCatalog catalog,
        IReadOnlySet<SelectorValue> grantedSelectors,
        bool shortCircuit)
    {
        if (!marking.HasSelectors)
        {
            return [];
        }

        var checks = new List<GateCheck>(marking.Selectors.Count * 2);
        foreach (var selector in marking.Selectors)
        {
            var eligibility = CheckEligibility(selector, principal, catalog);
            checks.Add(eligibility);
            if (shortCircuit && !eligibility.Passed)
            {
                return checks;
            }
        }

        foreach (var selector in marking.Selectors)
        {
            var grant = CheckGrant(selector, grantedSelectors);
            checks.Add(grant);
            if (shortCircuit && !grant.Passed)
            {
                return checks;
            }
        }

        return checks;
    }

    /// <summary>The enforcement form: the first failing selector gate's reason, in the
    /// order the class doc gives, or allow when every selector passes both.</summary>
    public static PermissionCheckResult Check(
        ProtectiveMarking marking, Principal principal, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        GateCheck.ResultOf(Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit: true));

    /// <summary>The inspector form: every E check, then every G check, each evaluated
    /// regardless of the others. Empty for a marking with no selectors.</summary>
    public static IReadOnlyList<GateCheck> CheckAll(
        ProtectiveMarking marking, Principal principal, SelectorCatalog catalog, IReadOnlySet<SelectorValue> grantedSelectors) =>
        Evaluate(marking, principal, catalog, grantedSelectors, shortCircuit: false);

    /// <summary>E for one selector.</summary>
    public static GateCheck CheckEligibility(SelectorValue selector, Principal principal, SelectorCatalog catalog)
    {
        if (!catalog.TryGet(selector.Category, out var category))
        {
            return Fail(GateKind.SelectorEligibility, UnknownCategoryReasonPrefix, selector);
        }

        return IsEligible(principal, category)
            ? Pass(GateKind.SelectorEligibility, selector)
            : Fail(GateKind.SelectorEligibility, NotEligibleReasonPrefix, selector);
    }

    /// <summary>G for one selector. No catalog consultation, on purpose: a grant can only
    /// carry catalog values at write time, and a value whose category was later removed
    /// from configuration has already failed E.</summary>
    public static GateCheck CheckGrant(SelectorValue selector, IReadOnlySet<SelectorValue> grantedSelectors) =>
        grantedSelectors.Contains(selector)
            ? Pass(GateKind.SelectorGrant, selector)
            : Fail(GateKind.SelectorGrant, NotGrantedReasonPrefix, selector);

    private static GateCheck Pass(GateKind kind, SelectorValue selector) =>
        new(kind, true, null, selector.Category, selector.Value);

    private static GateCheck Fail(GateKind kind, string reasonPrefix, SelectorValue selector) =>
        new(kind, false, reasonPrefix + selector.Category, selector.Category, selector.Value);
}
