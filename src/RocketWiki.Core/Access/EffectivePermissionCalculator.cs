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
            if (rule.Kind != AccessRuleKind.PageRestriction || rule.Action != action)
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.PageRestriction, result);
            if (!result.IsMatch)
            {
                return PermissionCheckResult.Deny($"restriction:{rule.PageId}:{rule.Id}");
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
    public static EffectivePermission Compute(
        IEnumerable<AccessRule> spaceGrants,
        IEnumerable<AccessRule> pageAndAncestorRestrictions,
        bool isReplicaSpace,
        Principal principal)
    {
        // design.md §15: timed and counted, never traced with a span — canView runs on
        // every resolved Page (§6.7), so a span per check would be one per field, and
        // the interesting question is a distribution ("are permission checks slow, are
        // denials spiking"), which a histogram answers without a per-decision record.
        var startTimestamp = Stopwatch.GetTimestamp();
        var permission = ComputeCore(spaceGrants, pageAndAncestorRestrictions, isReplicaSpace, principal);
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
        Principal principal)
    {
        var role = ComputeSpaceRole(spaceGrants, principal);
        var restrictions = pageAndAncestorRestrictions as IReadOnlyCollection<AccessRule>
            ?? pageAndAncestorRestrictions.ToList();

        var viewChecks = EvaluateAll(restrictions, PageAction.View, principal);
        var editChecks = EvaluateAll(restrictions, PageAction.Edit, principal);

        var permission = DeriveVerdict(role, isReplicaSpace, viewChecks, editChecks);
        return new EffectivePermissionExplanation(role, isReplicaSpace, permission, viewChecks, editChecks);
    }

    private static IReadOnlyList<RestrictionCheckDetail> EvaluateAll(
        IReadOnlyCollection<AccessRule> restrictions, PageAction action, Principal principal)
    {
        var checks = new List<RestrictionCheckDetail>();
        foreach (var rule in restrictions)
        {
            if (rule.Kind != AccessRuleKind.PageRestriction || rule.Action != action || rule.PageId is null)
            {
                continue;
            }

            var result = AccessRuleExpression.Evaluate(rule.ExpressionJson, principal);
            CoreTelemetry.RecordRuleEvaluation(AccessRuleKind.PageRestriction, result);
            checks.Add(new RestrictionCheckDetail(rule.Id, rule.PageId.Value, action, rule.ExpressionJson, result.IsMatch));
        }

        return checks;
    }

    /// <summary>Mirrors <see cref="ComputeCore"/>'s precedence exactly, over
    /// already-evaluated checks. Kept adjacent to ComputeCore on purpose; the
    /// Explain-agrees-with-Compute test fails if these two ever diverge.</summary>
    private static EffectivePermission DeriveVerdict(
        SpaceRole? role,
        bool isReplicaSpace,
        IReadOnlyList<RestrictionCheckDetail> viewChecks,
        IReadOnlyList<RestrictionCheckDetail> editChecks)
    {
        if (role is null)
        {
            return new EffectivePermission(false, false, "no-space-role", "no-space-role");
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
        Principal principal)
    {
        var role = ComputeSpaceRole(spaceGrants, principal);
        if (role is null)
        {
            return new EffectivePermission(false, false, "no-space-role", "no-space-role");
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
