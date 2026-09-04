using System.Text.Json;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// design.md §6.6's permission inspector: "why can / can't user X see this page" —
    /// the space access and role computations and every gate with pass/fail (S, C, E,
    /// G, N and each view restriction; replica, role and each edit restriction),
    /// computed by the calculator's non-short-circuiting Explain path (never a
    /// relaxation of the enforcement gate; a Core test pins the two to identical
    /// verdicts).
    ///
    /// Two modes, one field:
    /// <list type="bullet">
    /// <item><b>Self</b> (no <paramref name="subject"/>): any authenticated caller,
    /// gated on their own canView of the page — an inspector aimed at a page you
    /// cannot view would itself be the §6.7 leak (confirming existence AND handing
    /// over the failing rule with its expression), so a non-viewable page is null
    /// exactly like a nonexistent one, with the denial audited first. The
    /// self-explanation surface for a page you cannot view is <c>pageAccess.denial</c>
    /// (§21.8), which discloses the gates and never an expression or an ancestor
    /// title.</item>
    /// <item><b>Foreign</b> (a principal-shaped <paramref name="subject"/>): instance
    /// admins only — inspecting someone else's access is an admin diagnostic. The
    /// subject is supplied as groups/attributes rather than looked up, because
    /// authorization only ever evaluates token-shaped principals (design.md §6.1) and
    /// no other user's token is available here; this doubles as the what-if tester
    /// §6.6's tooling needs, and a subject's nationality (§21.4) is stated
    /// through the same <c>attributes</c> input as any other claim. The ADMIN must also
    /// pass canView on the page: §6.5's no-read-around is absolute, and an inspector
    /// that showed a non-viewing admin a page's rules and ancestor titles would be
    /// exactly the silent read-around it forbids (§6.4.1 hides even titles from admins
    /// in delete refusals). The sanctioned path for an admin locked out of a page is
    /// unchanged: change the rules — which is audited — then inspect.</item>
    /// </list>
    ///
    /// Audit (§7): every inspection is a distinct <c>permission.inspect</c> event —
    /// success and denial, self and foreign — written explicitly here because the
    /// field middleware could not derive the page subject from this return type.
    /// Deliberately NOT ridden on page.view: the per-request dedup would swallow the
    /// inspection whenever the same request also viewed the page, making inspector
    /// usage — an access-probing signal §7 explicitly wants visible — invisible
    /// exactly when it happens most. Details carry the inspected principal's user id
    /// (a reference) and the denial reason's rule/page ids; NEVER the subject's
    /// groups or attribute values — §7 sanctions rule contents in audit details, not
    /// a person's attributes (§6.2 marks those sensitive), and the reason strings'
    /// rule ids stay out of telemetry entirely (spans/metrics see nothing from this
    /// resolver; the calculator's own bounded counters are unchanged — §15).
    /// Known dedup edge, accepted and stated: two inspections of the *same page* with
    /// *different subjects* in one GraphQL request produce one success row
    /// (DbAuditSink keys on action+subject+outcome, not details).
    /// </summary>
    [AuditAction("permission.inspect")]
    public async Task<EffectivePermissionDetail?> EffectivePermission(
        Guid pageId,
        InspectedPrincipalInput? subject,
        [Service] IPagePermissionReadService permissionReadService,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var caller = principalAccessor.Current;
        if (caller is null)
        {
            // Anonymous: nothing to inspect with, nothing audited (no acting user
            // exists to attribute a row to) - same stance as Query.page.
            return null;
        }

        var self = subject is null;
        var inspectedUserId = subject?.UserId ?? caller.UserId;

        if (!self && !instanceRoleAccessor.IsInstanceAdmin)
        {
            // A non-admin probing the admin diagnostic is refused before any page
            // lookup - so this null confirms nothing about the page id - and the
            // probe itself is recorded (§7 "makes probing visible"). This is a real
            // refusal of a real request, unlike a not-found (which audits nothing).
            await RecordInspectionAsync(
                auditSink, AuditOutcome.Denied, pageId, inspectedUserId, self,
                reason: "instance-admin-required", cancellationToken);
            return null;
        }

        var subjectPrincipal = self
            ? caller
            : Principal.Create(
                subject!.UserId,
                subject.Groups ?? [],
                (subject.Attributes ?? []).Select(a =>
                    new KeyValuePair<string, IReadOnlyList<string>>(a.Key, a.Values ?? [])));

        var result = await permissionReadService.ExplainAsync(pageId, caller, subjectPrincipal, cancellationToken);
        switch (result)
        {
            case ReadResult<PagePermissionExplanation>.NotFound:
                // No such page: no access decision was made, so no audit row - and
                // null is byte-identical to the denied case below (§6.7).
                return null;

            case ReadResult<PagePermissionExplanation>.Denied denied:
                // The CALLER can't view the page (self or admin alike - see the
                // no-read-around note above). Audited with the caller's own failing
                // reason, then collapsed to the same null a missing page gets.
                await RecordInspectionAsync(
                    auditSink, AuditOutcome.Denied, pageId, inspectedUserId, self, denied.Reason, cancellationToken);
                return null;

            case ReadResult<PagePermissionExplanation>.Found found:
                await RecordInspectionAsync(
                    auditSink, AuditOutcome.Success, pageId, inspectedUserId, self, reason: null, cancellationToken);
                return EffectivePermissionDetail.From(found.Value, pageId);

            default:
                throw new InvalidOperationException($"Unexpected ReadResult case: {result.GetType().Name}.");
        }
    }

    private static Task RecordInspectionAsync(
        IAuditSink auditSink,
        AuditOutcome outcome,
        Guid pageId,
        string inspectedUserId,
        bool self,
        string? reason,
        CancellationToken cancellationToken)
    {
        // Same {"reason": ...} details key every other denial row uses, plus the
        // inspected principal REFERENCE - user id only, never the groups/attribute
        // values a foreign-subject input carries (see the field doc's §7 reasoning).
        var detailsJson = reason is null
            ? JsonSerializer.Serialize(new { inspectedUserId, self })
            : JsonSerializer.Serialize(new { reason, inspectedUserId, self });
        return auditSink.RecordAsync(
            new AuditRecord("permission.inspect", outcome, AuditSubjectType.Page, pageId, DetailsJson: detailsJson),
            cancellationToken);
    }
}

/// <summary>
/// The §6.6 inspector's answer, shaped to the SPA's
/// <c>web/src/access/permission/effectivePermissionTypes.ts</c> contract: the space
/// access and role computations, the verdict with the calculator's exact denial-reason
/// vocabulary (<c>no-space-access</c> / <c>marking:unavailable</c> /
/// <c>selector:{unknown|not_granted}:{CATEGORY}</c> /
/// <c>caveat:eyes_only</c> / <c>restriction:{pageId}:{ruleId}</c> /
/// <c>replica-read-only</c> / <c>insufficient-space-role</c> — what
/// describeDenialReason.ts parses), every gate's individual pass/fail, and every
/// restriction's with its chain page and expression.
///
/// <para><see cref="ViewGates"/> and <see cref="EditGates"/> are the same
/// <c>GateResult</c> shape a denied page's placeholder lists (§21.8) — one gate
/// vocabulary for both — while <see cref="ViewRestrictions"/> and
/// <see cref="EditRestrictions"/> keep the expression and chain-page title the
/// placeholder withholds: this surface is reachable only for a page the caller can
/// view, where those are the effective rules §6.6's banner already shows.
/// <see cref="SpaceRole"/> is ROLE grants only (§6.4); <see cref="HasSpaceAccess"/> the
/// ACCESS grants only, and the two are reported independently because a role without
/// access is a real, meaningful state (§6.5.2).</para>
/// </summary>
public sealed record EffectivePermissionDetail(
    string UserId,
    string UserDisplayName,
    bool HasSpaceAccess,
    SpaceRole? SpaceRole,
    bool IsReplicaSpace,
    bool CanView,
    bool CanEdit,
    string? ViewDenialReason,
    string? EditDenialReason,
    IReadOnlyList<GateResultView> ViewGates,
    IReadOnlyList<GateResultView> EditGates,
    IReadOnlyList<RestrictionCheckView> ViewRestrictions,
    IReadOnlyList<RestrictionCheckView> EditRestrictions)
{
    /// <summary>The marking is not in the explanation, so the gate rows carry no level or
    /// country set here; the page's own <c>marking</c> field sits beside this one and is
    /// the honest source for both. <paramref name="pageId"/> lets a restriction row say
    /// whether its rule is inherited from an ancestor.</summary>
    internal static EffectivePermissionDetail From(PagePermissionExplanation explanation, Guid pageId) => new(
        explanation.SubjectUserId,
        explanation.SubjectDisplayName,
        explanation.HasSpaceAccess,
        explanation.SpaceRole,
        explanation.IsReplicaSpace,
        explanation.Permission.CanView,
        explanation.Permission.CanEdit,
        explanation.Permission.ViewDenialReason,
        explanation.Permission.EditDenialReason,
        explanation.ViewGates.Select(g => GateResultView.From(g, marking: null, pageId)).ToList(),
        explanation.EditGates.Select(g => GateResultView.From(g, marking: null, pageId)).ToList(),
        explanation.ViewRestrictions.Select(RestrictionCheckView.From).ToList(),
        explanation.EditRestrictions.Select(RestrictionCheckView.From).ToList());
}

/// <summary>One evaluated restriction in the chain. `expressionJson` is the raw rule
/// JSON, same convention as AccessRule.expressionJson — the SPA parses it into its
/// RuleNode client-side.</summary>
public sealed record RestrictionCheckView(
    Guid RuleId,
    Guid PageId,
    string PageTitle,
    PageAction Action,
    string ExpressionJson,
    bool Passed)
{
    internal static RestrictionCheckView From(ExplainedRestriction r) =>
        new(r.RuleId, r.PageId, r.PageTitle, r.Action, r.ExpressionJson, r.Passed);
}

/// <summary>
/// A principal-shaped subject for foreign inspection (instance admins only): the
/// admin states who is being tested as the same three parts the rule engine
/// evaluates (design.md §6.1) — no lookup happens, because no other user's token
/// exists to build a real Principal from and the local mirror is display-only.
/// Groups/attribute values supplied here are evaluated and then discarded; only
/// <see cref="UserId"/> reaches the audit row.
/// </summary>
public sealed record InspectedPrincipalInput(
    string UserId,
    IReadOnlyList<string>? Groups,
    IReadOnlyList<InspectedAttributeInput>? Attributes);

/// <summary>One registered attribute (design.md §6.2) on the inspected subject, e.g.
/// key "nationality", values ["NZ","US"] for a dual national. Nationality is the only
/// attribute the marking gate reads (§21.4); any other key feeds rule expressions alone.</summary>
public sealed record InspectedAttributeInput(string Key, IReadOnlyList<string>? Values);
