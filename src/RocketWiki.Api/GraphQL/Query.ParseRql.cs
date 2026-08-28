using RocketWiki.Api.Audit;
using RocketWiki.Core.Query;

namespace RocketWiki.Api.GraphQL;

public partial class Query
{
    /// <summary>
    /// design.md §22: parses an RQL string and returns its syntax tree, its canonical
    /// printed form, and any errors with the positions to underline them.
    ///
    /// <para><b>This executes nothing and touches no page data.</b> <c>Rql.Parse</c> is a
    /// pure function of the string: no database, no clock, no principal. That is what makes
    /// the field safe to answer for any authenticated caller with no permission gate beyond
    /// authentication — and the reasoning is worth confirming rather than asserting, because
    /// "a parser needs no authorization" would be false the moment validation consulted the
    /// world. It does not, and cannot:</para>
    /// <list type="bullet">
    /// <item><b>It cannot reveal whether a space, label, or user exists.</b> Validation is
    /// syntax and closed vocabulary only, so <c>space = "BLACKPROJECT"</c> parses identically
    /// whether that space is real, invisible, or imaginary. design.md §6.7's
    /// indistinguishability holds here by construction — the validator has no DbContext to
    /// consult and no code path that could grow one without this doc becoming visibly
    /// wrong.</item>
    /// <item><b>It reveals nothing about the estate.</b> The closed field set and its error
    /// messages are compile-time vocabulary, identical for every caller — the same category
    /// as <c>protectiveMarkings</c> (§21's fixed ladder) and <c>pagePropertyKeys</c>.</item>
    /// <item><b>The one thing it does return about the input — the canonical form — is the
    /// caller's own text, normalized.</b></item>
    /// </list>
    ///
    /// <para><b>Audit (design.md §7):</b> <c>[NoAudit]</c>, and the justification is the
    /// above rather than convenience. §7 audits <i>user actions</i>, and an action has a
    /// subject and an access decision; this has neither. There is no page, space, or
    /// attachment to name, and no rule was evaluated for or against anybody, so an audit row
    /// could only record "someone typed a string" — indistinguishable from noise, and
    /// actively harmful to the audit table's usefulness, since an editor with live syntax
    /// checking would write a row per keystroke. Every query that actually <i>runs</i> is
    /// audited as <c>page.query</c>, which is where who-asked-what lives.</para>
    /// </summary>
    [NoAudit("Pure syntax service (design.md §22): parses a string with no database, clock, or principal, " +
             "resolves no space/label/user and so cannot reveal whether any exists (§6.7), and reads no wiki " +
             "content - there is no subject and no access decision for a §7 row to record. Executing a query " +
             "is audited as page.query; a live-syntax-checking editor calling this would otherwise write an " +
             "audit row per keystroke.")]
    public RqlParseResultView ParseRql(string query) => RqlParseResultView.From(Rql.Parse(query));
}
