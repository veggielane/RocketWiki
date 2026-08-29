using RocketWiki.Core.Access;

namespace RocketWiki.Core.Services;

/// <summary>
/// Usage reporting over the audit trail (design.md §7), for instance admins site-wide
/// and space admins within their own spaces.
///
/// <para><b>Every number is computed over the caller's visible set.</b> The audit table
/// records what happened to everything; a report must only ever count what this caller
/// could read for themselves. That set comes from the existing permission- and
/// marking-pruned page walk rather than a query written here, so the §21 clearance gate
/// and §6.4 restrictions are inherited rather than reimplemented — the same rule the
/// tree, search and RQL all follow. Two admins with different clearances therefore see
/// different totals, which is the gate working.</para>
///
/// <para><b>Classification is never a dimension.</b> No breakdown, filter or series is
/// keyed on marking level, caveat or restriction state. design.md refuses this in three
/// separate places (§21.8's telemetry tags, §21.13's aggregate labels, §22's query
/// fields) for one reason: a count bucketed by classification is a census of the
/// classified estate, and a chart is a more patient instrument than a query box. That
/// holds here even though the caller is an admin, because §21's gate subtracts from
/// admins too.</para>
/// </summary>
public interface IAnalyticsService
{
    /// <summary>
    /// The report for one space, or site-wide when <paramref name="spaceKey"/> is null.
    ///
    /// <para>Returns <see cref="ReadResult{T}.Denied"/> for a caller who administers
    /// neither the instance nor the named space, and <see cref="ReadResult{T}.NotFound"/>
    /// for a space key that does not resolve — collapsed by the caller into one answer,
    /// per §6.7, so this cannot be used to probe which space keys exist.</para>
    /// </summary>
    Task<ReadResult<AnalyticsReport>> GetReportAsync(
        string? spaceKey,
        DateTime fromUtc,
        DateTime toUtc,
        Principal principal,
        bool isInstanceAdmin,
        CancellationToken cancellationToken = default);
}
