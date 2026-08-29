using System.Diagnostics;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Telemetry;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.4: computes space role and effective view/edit permission for a page.
/// This is the only place these rules should be implemented — callers (GraphQL
/// resolvers, MCP tools, tree filtering, search post-filtering) must all route through
/// here rather than re-deriving the logic, so the invariants in this file's tests hold
/// everywhere canView/canEdit is checked (design.md §6.7).
///
/// Deliberately absent: any notion of "instance admin". design.md §6.5 requires that
/// instance admins do not bypass page restrictions, so no admin flag is threaded
/// through this calculator at all — there is nothing here for a caller to bypass with.
///
/// design.md §21 adds a second, independent gate on canView: the page's protective
/// marking versus the principal's clearance. It is applied HERE, inside the one
/// computation every read path already funnels through, rather than at each call site —
/// which is what makes "classification can only subtract, never grant" a structural
/// property. There is no parameter, overload, or flag by which a caller can obtain a
/// canView that skipped it, for the same reason there is no admin flag.
/// </summary>
public static class EffectivePermissionCalculator
{
    /// <summary>
    /// design.md §6.4: your role is the highest whose expression you satisfy; multiple
    /// grants OR together. Returns null if no grant matches (fails closed — no default role).
    /// </summary>
    public static SpaceRole? ComputeSpaceRole(IEnumerable<AccessRule> spaceGrants, Principal principal)
    {
        SpaceRole? best = null;
        foreach (var rule in spaceGrants)
        {
            if (rule.Kind != AccessRuleKind.SpaceGrant || rule.Role is null)
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.SpaceGrant, result);
            if (result.IsMatch && (best is null || rule.Role.Value > best.Value))
            {
                best = rule.Role.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// design.md §6.4: restrictions never widen access and accumulate down the page
    /// tree — every restriction for the given action, across the page and every
    /// ancestor, must pass. Pass the full page+ancestor restriction set for one
    /// action; the first failing restriction is reported for audit (design.md §7).
    /// </summary>
    public static PermissionCheckResult CheckRestrictions(
        IEnumerable<AccessRule> pageAndAncestorRestrictions,
        PageAction action,
        Principal principal)
    {
        foreach (var rule in pageAndAncestorRestrictions)
        {
            if (!AppliesTo(rule, action))
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.PageRestriction, result);
            if (!result.IsMatch)
            {
                return PermissionCheckResult.Deny(ReasonFor(rule));
            }
        }

        return PermissionCheckResult.Allow();
    }

    /// <summary>
    /// Full canView/canEdit computation for one page, per design.md §6.4. The replica
    /// invariant is applied after the view check and before any role/edit-restriction
    /// check, so it beats every grant unconditionally, per §6.4/§12.
    /// </summary>
    /// <param name="spaceGrants">All SpaceGrant AccessRules for the page's space.</param>
    /// <param name="pageAndAncestorRestrictions">
    /// All PageRestriction AccessRules attached to the page itself and to every one of
    /// its ancestors (design.md §6.4: restrictions accumulate down the tree).
    /// </param>
    /// <param name="isReplicaSpace">See <see cref="Space.IsReplicaOf"/>.</param>
    /// <param name="marking">
    /// design.md §21: the page's protective marking. <b>Required, not optional</b> —
    /// there is deliberately no nullable overload meaning "skip the check", because a
    /// call site that could pass null is a call site that can forget. A caller whose
    /// page has no marking row passes <see cref="ProtectiveMarking.FailClosed"/>; a
    /// caller computing permission for a chain that is not a page (a create under a
    /// parent) passes the marking the created page would inherit. Both decisions belong
    /// to the loader, not here.
    /// </param>
    public static EffectivePermission Compute(
        IEnumerable<AccessRule> spaceGrants,
        IEnumerable<AccessRule> pageAndAncestorRestrictions,
        bool isReplicaSpace,
        ProtectiveMarking marking,
        Principal principal)
    {
        // design.md §15: timed and counted, never traced with a span — canView runs on
        // every resolved Page (§6.7), so a span per check would be one per field, and
        // the interesting question is a distribution ("are permission checks slow, are
        // denials spiking"), which a histogram answers without a per-decision record.
        var startTimestamp = Stopwatch.GetTimestamp();
        var permission = ComputeCore(spaceGrants, pageAndAncestorRestrictions, isReplicaSpace, marking, principal);
        CoreTelemetry.RecordPermissionCheck(permission, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
        return permission;
    }

    /// <summary>
    /// design.md §6.6's inspector path: the non-short-circuiting sibling of
    /// <see cref="Compute"/>. The enforcement gate stops at the first failing
    /// restriction (correct and cheap); an inspector that stopped there could only
    /// ever show one reason, so this evaluates <b>every</b> restriction for both
    /// actions and reports each pass/fail — "a separate method, never a relaxation
    /// of the gate" (§6.6). The verdict it returns is derived from those same
    /// evaluations with exactly <see cref="ComputeCore"/>'s precedence, so the
    /// explanation can never disagree with the gate (pinned by test); a malformed
    /// rule reads as failed, exactly as the gate fails closed on it (§6.3).
    ///
    /// Callers must pass <paramref name="pageAndAncestorRestrictions"/> in a
    /// deterministic order (root-most ancestor first) so "the first failing
    /// restriction" — the reason string audit rows and this explanation both carry —
    /// is stable rather than database-enumeration-order luck.
    ///
    /// Telemetry: per-rule evaluations are counted exactly like the gate's (bounded
    /// kind/outcome tags only), but this method deliberately does NOT feed
    /// <c>RecordPermissionCheck</c> — that histogram answers "are enforcement checks
    /// slow / are denials spiking", and inspector traffic (an admin deliberately
    /// examining a denied principal) would pollute the denial-rate signal it exists
    /// to provide (design.md §15). No reason string or expression content reaches
    /// telemetry from here, same as everywhere else.
    /// </summary>
    public static EffectivePermissionExplanation Explain(
        IEnumerable<AccessRule> spaceGrants,
        IEnumerable<AccessRule> pageAndAncestorRestrictions,
        bool isReplicaSpace,
        ProtectiveMarking marking,
        Principal principal)
    {
        var role = ComputeSpaceRole(spaceGrants, principal);
        var restrictions = pageAndAncestorRestrictions as IReadOnlyCollection<AccessRule>
            ?? pageAndAncestorRestrictions.ToList();

        var viewChecks = EvaluateAll(restrictions, PageAction.View, principal);
        var editChecks = EvaluateAll(restrictions, PageAction.Edit, principal);

        var permission = DeriveVerdict(role, isReplicaSpace, marking, principal, viewChecks, editChecks);
        return new EffectivePermissionExplanation(role, isReplicaSpace, permission, viewChecks, editChecks);
    }

    private static IReadOnlyList<RestrictionCheckDetail> EvaluateAll(
        IReadOnlyCollection<AccessRule> restrictions, PageAction action, Principal principal)
    {
        var checks = new List<RestrictionCheckDetail>();
        foreach (var rule in restrictions)
        {
            if (!AppliesTo(rule, action))
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.PageRestriction, result);
            checks.Add(new RestrictionCheckDetail(
                rule.Id, rule.PageId ?? Guid.Empty, action, rule.ExpressionJson, result.IsMatch));
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

    /// <summary>Mirrors <see cref="ComputeCore"/>'s precedence exactly, over
    /// already-evaluated checks. Kept adjacent to ComputeCore on purpose; the
    /// Explain-agrees-with-Compute test fails if these two ever diverge.</summary>
    private static EffectivePermission DeriveVerdict(
        SpaceRole? role,
        bool isReplicaSpace,
        ProtectiveMarking marking,
        Principal principal,
        IReadOnlyList<RestrictionCheckDetail> viewChecks,
        IReadOnlyList<RestrictionCheckDetail> editChecks)
    {
        if (role is null)
        {
            return new EffectivePermission(false, false, "no-space-role", "no-space-role");
        }

        // Mirrors ComputeCore's position for the classification gate exactly.
        var clearance = ClearanceGate.Check(marking, principal);
        if (!clearance.IsAllowed)
        {
            return new EffectivePermission(false, false, clearance.DenialReason, clearance.DenialReason);
        }

        var failedView = viewChecks.FirstOrDefault(c => !c.Passed);
        if (failedView is not null)
        {
            var reason = $"restriction:{failedView.PageId}:{failedView.RuleId}";
            return new EffectivePermission(false, false, reason, reason);
        }

        if (isReplicaSpace)
        {
            return new EffectivePermission(true, false, null, "replica-read-only");
        }

        if (role.Value < SpaceRole.Editor)
        {
            return new EffectivePermission(true, false, null, "insufficient-space-role");
        }

        var failedEdit = editChecks.FirstOrDefault(c => !c.Passed);
        if (failedEdit is not null)
        {
            return new EffectivePermission(true, false, null, $"restriction:{failedEdit.PageId}:{failedEdit.RuleId}");
        }

        return new EffectivePermission(true, true, null, null);
    }

    private static EffectivePermission ComputeCore(
        IEnumerable<AccessRule> spaceGrants,
        IEnumerable<AccessRule> pageAndAncestorRestrictions,
        bool isReplicaSpace,
        ProtectiveMarking marking,
        Principal principal)
    {
        var role = ComputeSpaceRole(spaceGrants, principal);
        if (role is null)
        {
            return new EffectivePermission(false, false, "no-space-role", "no-space-role");
        }

        // design.md §21: THE composition point. It sits after the space role and before
        // the restriction chain, and the placement is about which reason gets REPORTED,
        // never about the decision - both gates are conjuncts, so their order cannot
        // change any verdict. Reporting clearance ahead of a failing restriction is the
        // more actionable answer for a reviewer ("this principal has no business
        // reading this page at all" outranks "and also rule 7 said no"), and it is the
        // cheaper check, so a page the caller cannot be cleared for costs no rule
        // evaluations at all.
        //
        // Note what CANNOT be expressed here: there is no branch in which a grant, a
        // role, or a passing restriction causes this check to be skipped. canEdit is
        // reached only by falling through canView, so an editor or space-admin who
        // fails clearance gets (false, false) like anyone else, and canEdit's own
        // marking constraint (you may not mark above your clearance) is enforced in
        // PageMarkingService on top of this.
        var clearance = ClearanceGate.Check(marking, principal);
        if (!clearance.IsAllowed)
        {
            return new EffectivePermission(false, false, clearance.DenialReason, clearance.DenialReason);
        }

        var restrictions = pageAndAncestorRestrictions as IReadOnlyCollection<AccessRule>
            ?? pageAndAncestorRestrictions.ToList();

        var viewCheck = CheckRestrictions(restrictions, PageAction.View, principal);
        if (!viewCheck.IsAllowed)
        {
            return new EffectivePermission(false, false, viewCheck.DenialReason, viewCheck.DenialReason);
        }

        if (isReplicaSpace)
        {
            return new EffectivePermission(true, false, null, "replica-read-only");
        }

        if (role.Value < SpaceRole.Editor)
        {
            return new EffectivePermission(true, false, null, "insufficient-space-role");
        }

        var editCheck = CheckRestrictions(restrictions, PageAction.Edit, principal);
        if (!editCheck.IsAllowed)
        {
            return new EffectivePermission(true, false, null, editCheck.DenialReason);
        }

        return new EffectivePermission(true, true, null, null);
    }
}
