using System.Diagnostics;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.4: computes space access, space role and effective view/edit permission
/// for a page. This is the only place these rules should be implemented — callers
/// (GraphQL resolvers, MCP tools, tree filtering, search post-filtering) must all route
/// through here rather than re-deriving the logic, so the invariants in this file's tests
/// hold everywhere canView/canEdit is checked (design.md §6.7).
///
/// Deliberately absent: any notion of "instance admin". design.md §6.5 requires that
/// instance admins do not bypass page restrictions, so no admin flag is threaded
/// through this calculator at all — there is nothing here for a caller to bypass with.
///
/// <para><b>One ladder.</b> canView is the conjunction, in <i>reporting</i> order, of
/// space access (S), the marking's availability, selector grant (G), national caveat (N)
/// and the view-restriction chain (R); canEdit adds the replica invariant, a role grant
/// of Editor or above, and the edit-restriction chain (§6.4/§21.2). Both
/// <see cref="Compute"/> and <see cref="Explain"/> walk the same ladder through the same
/// private method — the enforcement form stops at the first failure, the inspector form
/// evaluates everything — so the two can never disagree about a verdict or about which
/// reason names it. The order changes no verdict (every gate is a conjunct); it decides
/// which token an audit row carries, and the rule is "the coarser fact first": a
/// principal no grant admits is not told what selector, caveat or restriction they would
/// also have failed (§6.7). The ladder used to hold two more rungs — clearance against
/// the level, and a per-category eligibility claim — both reading Keycloak attributes
/// this deployment does not carry; see <see cref="MarkingGate"/> for why they went.</para>
///
/// <para><b>Two grant kinds, two questions.</b> An access grant answers "may this
/// principal see the space" (<see cref="ComputeSpaceAccess"/>) and carries the selector
/// values it confers; a role grant answers "may they edit or administer it"
/// (<see cref="ComputeSpaceRole"/>) and confers no visibility. <b>Roles never supersede
/// access</b> (§6.4): a Space-admin with no matching access grant manages a space whose
/// pages they cannot read (§6.5.2), and there is no branch here in which a role stands in
/// for an access grant. Only the space <i>listing</i> shows such a principal the space at
/// all (<see cref="IsSpaceVisible"/>), so they can reach the grants they administer.</para>
///
/// <para>design.md §21: the marking gates are applied HERE, inside the one computation
/// every read path already funnels through, rather than at each call site — which is what
/// makes "a marking can only subtract, never grant" a structural property. There is
/// no parameter, overload, or flag by which a caller can obtain a canView that skipped
/// them, for the same reason there is no admin flag; and the catalog arrives inside
/// <see cref="PermissionInputs"/> so a caller cannot forget it.</para>
/// </summary>
public static class EffectivePermissionCalculator
{
    /// <summary>S failed: no access grant in the space admits the principal (design.md §6.4/§6.7).</summary>
    public const string NoSpaceAccessReason = "no-space-access";

    /// <summary>The edit ladder's role gate failed: no role grant of Editor or above.</summary>
    public const string InsufficientSpaceRoleReason = "insufficient-space-role";

    /// <summary>The replica invariant (design.md §12): canEdit is false beneath every grant.</summary>
    public const string ReplicaReadOnlyReason = "replica-read-only";

    /// <summary>
    /// design.md §6.4 / §21.15: the S gate's positive answer. Evaluates only access
    /// grants (<see cref="AccessRuleKind.AccessGrant"/>); any match admits the principal
    /// to the space, and the result carries the <b>union</b> of the selector values every
    /// matching grant confers. Null when no access grant matches — fail closed, no default
    /// access. Role grants and restrictions in the list are ignored, not an error, so a
    /// caller may pass a space's whole grant list.
    /// </summary>
    public static SpaceAccess? ComputeSpaceAccess(IEnumerable<AccessRule> spaceGrants, Principal principal)
    {
        HashSet<SelectorValue>? granted = null;
        foreach (var rule in spaceGrants)
        {
            if (rule.Kind != AccessRuleKind.AccessGrant)
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.AccessGrant, result);
            if (result.IsMatch)
            {
                granted ??= [];
                granted.UnionWith(rule.SelectorValues());
            }
        }

        return granted is null ? null : new SpaceAccess(granted);
    }

    /// <summary>
    /// design.md §6.4: your role is the highest whose expression you satisfy; multiple
    /// role grants OR together. Returns null if no role grant matches (fails closed — no
    /// default role). Evaluates only role grants (<see cref="AccessRuleKind.RoleGrant"/>);
    /// a role confers no visibility — see <see cref="ComputeSpaceAccess"/> for that.
    /// </summary>
    public static SpaceRole? ComputeSpaceRole(IEnumerable<AccessRule> spaceGrants, Principal principal)
    {
        SpaceRole? best = null;
        foreach (var rule in spaceGrants)
        {
            if (rule.Kind != AccessRuleKind.RoleGrant || rule.Role is null)
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.RoleGrant, result);
            if (result.IsMatch && (best is null || rule.Role.Value > best.Value))
            {
                best = rule.Role.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// The S gate as a boolean, for the sites that ask "may this principal see anything
    /// in this space at all" before looking at a page — the tree's space-level check, the
    /// RQL space filter, label listings, watching a space, notification re-checks. Access
    /// grants only: a role grant confers no visibility (§6.4, "roles never supersede
    /// access"), and every site that calls this inherits that. Do not re-derive this test
    /// at a call site, and do not reach for <see cref="IsSpaceVisible"/> where content is
    /// at stake.
    /// </summary>
    public static bool HasSpaceAccess(IEnumerable<AccessRule> spaceGrants, Principal principal) =>
        ComputeSpaceAccess(spaceGrants, principal) is not null;

    /// <summary>
    /// Whether a space is <i>listed</i> for the principal and resolvable by key: an access
    /// grant OR any role grant matches (design.md §6.4/§6.5.2). This exists for exactly one
    /// reason — a Space-admin who holds no access grant must still be able to reach the
    /// space's settings and the grants they administer, otherwise the space they manage is
    /// invisible to them — and it is used at exactly those sites: the space listing, the
    /// by-key lookup, and whatever is built on them (MCP <c>list_spaces</c>, the presence
    /// space room). Nothing that returns page content may use it; content fields still
    /// require <see cref="HasSpaceAccess"/> and a page still requires the full ladder.
    /// </summary>
    public static bool IsSpaceVisible(IEnumerable<AccessRule> spaceGrants, Principal principal)
    {
        var grants = spaceGrants as IReadOnlyCollection<AccessRule> ?? spaceGrants.ToList();
        return ComputeSpaceAccess(grants, principal) is not null || ComputeSpaceRole(grants, principal) is not null;
    }

    /// <summary>
    /// design.md §6.4: restrictions never widen access and accumulate down the page
    /// tree — every restriction for the given action, across the page and every
    /// ancestor, must pass. Pass the full page+ancestor restriction set for one
    /// action; the first failing restriction is reported for audit (design.md §7).
    /// The enforcement form of the R gate on its own; the ladder uses the same
    /// evaluation through <see cref="EvaluateRestrictions"/>.
    /// </summary>
    public static PermissionCheckResult CheckRestrictions(
        IEnumerable<AccessRule> pageAndAncestorRestrictions,
        PageAction action,
        Principal principal) =>
        GateCheck.ResultOf(EvaluateRestrictions(pageAndAncestorRestrictions, action, principal, shortCircuit: true));

    /// <summary>
    /// Full canView/canEdit computation for one page, per design.md §6.4 — the
    /// enforcement form, which stops at the first failing gate. The replica invariant is
    /// applied after the view ladder and before any role/edit-restriction check, so it
    /// beats every grant unconditionally, per §6.4/§12.
    /// </summary>
    /// <param name="inputs">The five facts the decision is made from; see
    /// <see cref="PermissionInputs"/> for who assembles them and how.</param>
    public static EffectivePermission Compute(PermissionInputs inputs, Principal principal)
    {
        // design.md §15: timed and counted, never traced with a span — canView runs on
        // every resolved Page (§6.7), so a span per check would be one per field, and
        // the interesting question is a distribution ("are permission checks slow, are
        // denials spiking"), which a histogram answers without a per-decision record.
        var startTimestamp = Stopwatch.GetTimestamp();
        var gates = EvaluateGates(inputs, principal, shortCircuit: true);
        var permission = VerdictFrom(gates.ViewGates, gates.EditGates);
        CoreTelemetry.RecordPermissionCheck(permission, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
        return permission;
    }

    /// <summary>
    /// design.md §6.6's inspector path: the non-short-circuiting form of
    /// <see cref="Compute"/>. The enforcement gate stops at the first failure (correct and
    /// cheap); an inspector that stopped there could only ever show one reason, so this
    /// evaluates <b>every</b> gate — the marking gates and both restriction chains even
    /// when space access has already failed — and reports each pass/fail: "a separate
    /// method, never a relaxation of the gate" (§6.6). The verdict it returns is derived
    /// from those same evaluations with exactly <see cref="Compute"/>'s precedence, so the
    /// explanation can never disagree with the gate (pinned by test); a malformed rule
    /// reads as failed, exactly as the gate fails closed on it (§6.3).
    ///
    /// Callers must pass <see cref="PermissionInputs.ChainRestrictions"/> in a
    /// deterministic order (root-most ancestor first) so "the first failing restriction" —
    /// the reason string audit rows and this explanation both carry — is stable rather than
    /// database-enumeration-order luck.
    ///
    /// Telemetry: per-rule evaluations are counted exactly like the gate's (bounded
    /// kind/outcome tags only), but this method deliberately does NOT feed
    /// <c>RecordPermissionCheck</c> — that histogram answers "are enforcement checks
    /// slow / are denials spiking", and inspector traffic (an admin deliberately
    /// examining a denied principal) would pollute the denial-rate signal it exists
    /// to provide (design.md §15). No reason string or expression content reaches
    /// telemetry from here, same as everywhere else.
    /// </summary>
    public static EffectivePermissionExplanation Explain(PermissionInputs inputs, Principal principal)
    {
        var gates = EvaluateGates(inputs, principal, shortCircuit: false);
        return new EffectivePermissionExplanation(
            gates.Access is not null,
            gates.Access?.GrantedSelectors ?? SpaceAccess.WithoutSelectors.GrantedSelectors,
            gates.Role,
            inputs.IsReplicaSpace,
            VerdictFrom(gates.ViewGates, gates.EditGates),
            gates.ViewGates,
            gates.EditGates);
    }

    /// <summary>
    /// The view ladder after S — availability, G, N through <see cref="MarkingGate"/>, then
    /// the view restrictions in <paramref name="restrictions"/> — as one list of gate checks.
    /// Exactly the view half of the ladder <see cref="Compute"/> runs, exposed so the
    /// tree walk can decide each node with the same gates (§21.9): the walk carries
    /// ancestor restrictions down the recursion and so passes a node's <i>own</i> rules,
    /// where <see cref="Compute"/> passes the whole chain. A parity test pins that the two
    /// agree for the same inputs.
    /// </summary>
    /// <param name="access">The caller's space access, already established (S passed).
    /// A caller who has none never reaches this gate on the enforcement path; the
    /// inspector passes <see cref="SpaceAccess.WithoutSelectors"/> to list what would have
    /// failed.</param>
    /// <param name="shortCircuit">True stops at the first failing gate (enforcement);
    /// false evaluates every gate (inspection, and a denied tree node's full reason list).</param>
    public static IReadOnlyList<GateCheck> EvaluateViewGates(
        SpaceAccess access,
        ProtectiveMarking marking,
        IReadOnlyList<AccessRule> restrictions,
        SelectorCatalog catalog,
        Principal principal,
        bool shortCircuit)
    {
        // design.md §21: THE composition point for the marking. It sits after space access
        // and before the restriction chain, and the placement is about which reason gets
        // REPORTED, never about the decision - both are conjuncts, so their order cannot
        // change any verdict. Reporting the marking ahead of a failing restriction is the
        // more actionable answer for a reviewer ("this principal has no business reading
        // this page at all" outranks "and also rule 7 said no"), and it is the cheaper
        // check, so a page the caller is not granted costs no rule evaluations.
        var checks = new List<GateCheck>(MarkingGate.Evaluate(marking, principal, catalog, access.GrantedSelectors, shortCircuit));
        if (shortCircuit && checks.Any(c => !c.Passed))
        {
            return checks;
        }

        checks.AddRange(EvaluateRestrictions(restrictions, PageAction.View, principal, shortCircuit));
        return checks;
    }

    /// <summary>The ladder's raw output: what S and the role computation found, and every
    /// gate check produced on each side.</summary>
    private readonly record struct GateEvaluation(
        SpaceAccess? Access,
        SpaceRole? Role,
        IReadOnlyList<GateCheck> ViewGates,
        IReadOnlyList<GateCheck> EditGates);

    /// <summary>
    /// THE ladder (design.md §6.4/§21.2). S first: the outer boundary. Then the view half
    /// (<see cref="EvaluateViewGates"/>), then — only reachable through a passing view —
    /// the edit half: replica, role, edit restrictions. In the enforcement form
    /// (<paramref name="shortCircuit"/>) each half stops at its first failure and the edit
    /// half is not entered at all when the view half failed, so canEdit is reached only by
    /// falling through canView; in the inspector form everything is evaluated, with an
    /// empty granted-selector set standing in when S failed.
    ///
    /// <para>Note what CANNOT be expressed here: there is no branch in which a grant, a
    /// role, or a passing restriction causes a marking gate to be skipped. An editor or
    /// space-admin who fails a selector gets (false, false) like anyone else, and canEdit's
    /// own marking constraint (you may not mark a page you could not then read) is
    /// enforced in <c>PageMarkingService</c> on top of this through the same
    /// <see cref="MarkingGate"/>.</para>
    /// </summary>
    private static GateEvaluation EvaluateGates(PermissionInputs inputs, Principal principal, bool shortCircuit)
    {
        var access = ComputeSpaceAccess(inputs.SpaceGrants, principal);
        var viewGates = new List<GateCheck>
        {
            access is null
                ? GateCheck.Fail(GateKind.SpaceAccess, NoSpaceAccessReason)
                : GateCheck.Pass(GateKind.SpaceAccess),
        };

        if (access is null && shortCircuit)
        {
            return new GateEvaluation(null, null, viewGates, []);
        }

        viewGates.AddRange(EvaluateViewGates(
            access ?? SpaceAccess.WithoutSelectors, inputs.Marking, inputs.ChainRestrictions, inputs.Catalog, principal, shortCircuit));

        if (shortCircuit && viewGates.Any(c => !c.Passed))
        {
            // The edit half is unreachable: canEdit falls through canView (§6.4), and a
            // role computed here would be a fact the verdict never uses.
            return new GateEvaluation(access, null, viewGates, []);
        }

        var editGates = new List<GateCheck>
        {
            inputs.IsReplicaSpace
                ? GateCheck.Fail(GateKind.ReplicaReadOnly, ReplicaReadOnlyReason)
                : GateCheck.Pass(GateKind.ReplicaReadOnly),
        };
        if (inputs.IsReplicaSpace && shortCircuit)
        {
            return new GateEvaluation(access, null, viewGates, editGates);
        }

        // Editing needs a ROLE grant (Editor or above) on top of seeing: an access grant
        // alone is the read-only state "viewer" used to name (§6.4). Editor is the floor
        // of the SpaceRole enum, so any role at all is enough here.
        var role = ComputeSpaceRole(inputs.SpaceGrants, principal);
        editGates.Add(role is null
            ? GateCheck.Fail(GateKind.SpaceRole, InsufficientSpaceRoleReason)
            : GateCheck.Pass(GateKind.SpaceRole));
        if (role is null && shortCircuit)
        {
            return new GateEvaluation(access, role, viewGates, editGates);
        }

        editGates.AddRange(EvaluateRestrictions(inputs.ChainRestrictions, PageAction.Edit, principal, shortCircuit));
        return new GateEvaluation(access, role, viewGates, editGates);
    }

    /// <summary>
    /// The verdict, from the gate lists alone: the first failing view gate denies both
    /// (its reason in both fields — canEdit falls through canView, §6.4); otherwise the
    /// first failing edit gate denies edit; otherwise allow. Stated once and used by both
    /// forms, which is what makes "Explain agrees with Compute" true by construction
    /// rather than by discipline.
    /// </summary>
    private static EffectivePermission VerdictFrom(IReadOnlyList<GateCheck> viewGates, IReadOnlyList<GateCheck> editGates)
    {
        var failedView = viewGates.FirstOrDefault(c => !c.Passed);
        if (failedView is not null)
        {
            return new EffectivePermission(false, false, failedView.Reason, failedView.Reason);
        }

        var failedEdit = editGates.FirstOrDefault(c => !c.Passed);
        if (failedEdit is not null)
        {
            return new EffectivePermission(true, false, null, failedEdit.Reason);
        }

        return new EffectivePermission(true, true, null, null);
    }

    /// <summary>
    /// The R gate for one action: every restriction that applies, in the order given,
    /// each as a gate check carrying the rule id, chain page, action and expression (the
    /// inspector's material, §6.6). Stops at the first failure when
    /// <paramref name="shortCircuit"/>. Per-rule telemetry is identical in both forms
    /// (bounded kind/outcome tags only, §15).
    /// </summary>
    private static IReadOnlyList<GateCheck> EvaluateRestrictions(
        IEnumerable<AccessRule> restrictions, PageAction action, Principal principal, bool shortCircuit)
    {
        var kind = action == PageAction.Edit ? GateKind.EditRestriction : GateKind.ViewRestriction;
        var checks = new List<GateCheck>();
        foreach (var rule in restrictions)
        {
            if (!AppliesTo(rule, action))
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.PageRestriction, result);
            checks.Add(new GateCheck(
                kind,
                result.IsMatch,
                result.IsMatch ? null : ReasonFor(rule),
                RuleId: rule.Id,
                PageId: rule.PageId ?? Guid.Empty,
                Action: action,
                ExpressionJson: rule.ExpressionJson));
            if (!result.IsMatch && shortCircuit)
            {
                return checks;
            }
        }

        return checks;
    }

    /// <summary>
    /// THE filter deciding whether a rule participates in an action's chain, shared by the
    /// gate and the inspector so the two cannot disagree about which rules exist.
    ///
    /// <para>It used to be written out twice, and the copies had drifted: the inspector
    /// additionally skipped a restriction with a null <c>PageId</c> while the gate
    /// evaluated it. Unreachable through the database — <c>CK_AccessRules_KindColumnPairing</c>
    /// makes a PageRestriction without a PageId unrepresentable, and
    /// <c>PermissionContextLoader</c> filters them out besides — but "the explanation can
    /// never disagree with the gate" is an invariant pinned by test, and a divergence
    /// living in two hand-copied conditions is how it would eventually stop being true.
    /// Now a null PageId denies in the gate and appears as a failed check in the
    /// inspector, which are the same verdict rendered two ways.</para>
    /// </summary>
    private static bool AppliesTo(AccessRule rule, PageAction action) =>
        rule.Kind == AccessRuleKind.PageRestriction && rule.Action == action;

    /// <summary>The one spelling of a failed restriction's reason (design.md §7):
    /// <c>restriction:{pageId}:{ruleId}</c>. Shared so the gate's audit row and the
    /// inspector's explanation are byte-identical, including for the corrupt-row case
    /// <see cref="AppliesTo"/> describes.</summary>
    private static string ReasonFor(AccessRule rule) => $"restriction:{rule.PageId ?? Guid.Empty}:{rule.Id}";
}
