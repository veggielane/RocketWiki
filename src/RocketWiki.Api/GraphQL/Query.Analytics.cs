using System.Security.Claims;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// Usage reporting for instance admins (site-wide, <paramref name="spaceKey"/>
    /// null) and space admins (their own spaces). Null for anyone else, and for a
    /// space key that does not resolve — one answer for both, per §6.7, so this is
    /// not a way to ask which keys exist.
    ///
    /// <para><b>Audited on purpose, and not as a formality.</b> This report names who
    /// read what, at the instance owner's explicit direction. design.md §15 calls an
    /// unregulated record of who-reads-what exactly the thing telemetry must never
    /// become; the answer to building one deliberately is that reading it is itself an
    /// event. `analytics.view` records the scope and window, so "who looked at the
    /// reading habits" is as answerable as "who read the page".</para>
    ///
    /// <para>The report is computed over the caller's own visible set, so two admins
    /// with different grants see different totals (§21 subtracts from admins too),
    /// and no series anywhere is keyed on classification — see IAnalyticsService.</para>
    /// </summary>
    /// <remarks>
    /// Declared here AND recorded explicitly below, exactly as `search.query` is: the
    /// middleware cannot know the window and scope belong in the details, and the row
    /// written below wins the per-request dedup inside DbAuditSink, so one request
    /// still produces exactly one `analytics.view` row — the richer one.
    /// </remarks>
    [AuditAction("analytics.view")]
    [UseAuditDispatch]
    public async Task<AnalyticsReport?> Analytics(
        string? spaceKey,
        DateTime fromUtc,
        DateTime toUtc,
        ClaimsPrincipal claimsPrincipal,
        [Service] IAnalyticsService analytics,
        [Service] ICurrentPrincipalAccessor principalAccessor,
        [Service] IInstanceRoleAccessor instanceRoleAccessor,
        [Service] IAuditSink auditSink,
        CancellationToken cancellationToken)
    {
        var principal = principalAccessor.Current;
        if (principal is null || claimsPrincipal.Identity?.IsAuthenticated != true)
        {
            // Anonymous gets the same absent shape every read root gives, and writes no
            // audit row — there is no acting user to attribute one to (§7).
            return null;
        }

        var result = await analytics.GetReportAsync(
            spaceKey, fromUtc, toUtc, principal, instanceRoleAccessor.IsInstanceAdmin, cancellationToken);

        if (result is not ReadResult<AnalyticsReport>.Found found)
        {
            // A refused report is recorded with its reason, like any other denial (§7),
            // and then collapses to the same null a missing space gets.
            if (result is ReadResult<AnalyticsReport>.Denied denied)
            {
                await auditSink.RecordAsync(
                    new AuditRecord(
                        "analytics.view", AuditOutcome.Denied, AuditSubjectType.Space, null, spaceKey,
                        DetailsJson: Describe(spaceKey, fromUtc, toUtc, denied.Reason)),
                    cancellationToken);
            }

            return null;
        }

        await auditSink.RecordAsync(
            new AuditRecord(
                "analytics.view", AuditOutcome.Success, AuditSubjectType.Space, null, spaceKey,
                DetailsJson: Describe(spaceKey, fromUtc, toUtc, reason: null)),
            cancellationToken);

        return found.Value;
    }

    /// <summary>The window and scope the report covered, so an auditor can tell a
    /// glance at last week from a sweep of the whole year.</summary>
    private static string Describe(string? spaceKey, DateTime fromUtc, DateTime toUtc, string? reason) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            scope = spaceKey ?? "site",
            fromUtc,
            toUtc,
            reason,
        });
}
