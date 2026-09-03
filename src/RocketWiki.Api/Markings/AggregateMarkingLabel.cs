using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Markings;

/// <summary>
/// design.md §21.13 — the marking of a <b>compilation</b>: an Ask-the-wiki answer, a page
/// of search results, an MCP payload. Standard aggregation doctrine, mechanized: a
/// compilation carries the classification of its most sensitive constituent, so an answer
/// drawn from a SECRET page is SECRET even though every word of it was written by a
/// model.
///
/// <para><b>It is a DISPLAY LABEL. It is not a <see cref="ProtectiveMarking"/>, it is not
/// stored, and it gates nothing.</b> That is the single most important sentence here, and
/// three things enforce it rather than ask for it:</para>
/// <list type="number">
/// <item><b>It lives in RocketWiki.Api.</b> <c>MarkingGate</c> and
/// <c>EffectivePermissionCalculator</c> are in RocketWiki.Core, every gated read service
/// is in RocketWiki.Data, and neither project references RocketWiki.Api. No enforcement
/// code <i>can</i> consult this type — not "does not", cannot. The same structural
/// argument that keeps <c>PermissionContextLoader</c> internal to RocketWiki.Data.</item>
/// <item><b>It answers no question.</b> There is no <c>Check</c>, no <c>Allows</c>, no
/// boolean verdict on it at all; it has a level, some selectors, some caveat sets, a
/// prefix toggle and a string.</item>
/// <item><b>Enforcement already happened, per source, before any of this ran.</b>
/// Retrieval and search run under the caller's own principal, so every contributing page
/// individually passed canView and the marking gate. The aggregate is computed
/// <i>after</i> that, from pages the caller was already shown, and exists solely to tell a
/// human what the text in front of them is. Routing it through the gate would be
/// meaningless (it would re-check things that already passed) and dangerous (it would
/// make a display concern load-bearing).</item>
/// </list>
///
/// <para><b>Why it must exist.</b> Before it, an Ask answer synthesized from a
/// <c>UK SECRET</c> page arrived with no marking at all, and a cleared user could
/// legitimately paste it somewhere that was not. The model launders the marking off the
/// content; this puts it back on.</para>
///
/// <para><b>Because it labels rather than enforces, it can say what the storage model
/// cannot — and it must.</b> A page's marking holds ONE eyes-only set. An aggregate can
/// have sources with different ones, and there is no honest single set:
/// <list type="bullet">
/// <item>The <i>union</i> — <c>UK/US EYES ONLY</c> — says either nationality suffices.
/// That is a widening, and it is false: the UK source is still UK-only.</item>
/// <item>The <i>intersection</i> is worse, and it is the reason this class has a long
/// comment. <c>{UK} ∩ {US}</c> is <b>empty</b>, and an empty eyes-only set in this model
/// means <i>no caveat at all</i> — so the two most restrictive inputs available would
/// produce the least restrictive possible output, silently. That is exactly the shape of
/// the level-0 trap <see cref="ProtectiveMarking.Create(ClassificationLevel, IEnumerable{string}?, IEnumerable{SelectorValue}?, string?)"/>
/// normalizes away (an uninitialized tinyint compares below every clearance and makes a
/// page readable by everybody): a value that reads as "nothing here" when it should read
/// as "everything here".</item>
/// </list>
/// So the caveat is a <b>truthful conjunction</b>: distinct source sets are LISTED —
/// <c>UK SECRET UK EYES ONLY, US EYES ONLY</c>, meaning a reader needs both — never
/// merged and never intersected. The corresponding invariant, pinned by test:
/// <b><see cref="EyesOnlySets"/> is empty if and only if no source carried a caveat.</b>
/// It is built by filtering for sources that <i>have</i> one, so there is no other path
/// to empty; nothing subtracts, nothing intersects, and no set-algebra result can reach
/// it.</para>
///
/// <para><b>Selectors are the UNION</b> (§21.15), and here the union IS the honest
/// reading, unlike the caveat: a selector only ever subtracts readers, so a compilation
/// touching an APPLE page and a BANANA page is material that needed both, and listing
/// both says exactly that. A page carries one value per category; an aggregate may carry
/// several, which is why the formatter takes the parts loose.</para>
///
/// <para><b>No sources yields no label at all</b> (<see cref="Of"/> returns null), not
/// OFFICIAL and not TOP SECRET. Nothing was shown, so there is nothing to mark. OFFICIAL
/// would assert a reviewed judgement about content that does not exist — §21.11's
/// specific complaint about the backfill — and TOP SECRET would invent a fact and put a
/// scary banner on an empty page. Fail-closed does not apply because nothing is being
/// closed: this decides nothing. Null is the honest answer and the consuming field is
/// nullable to carry it.</para>
/// </summary>
public sealed record AggregateMarkingLabel
{
    private AggregateMarkingLabel(
        ClassificationLevel level,
        IReadOnlyList<SelectorValue> selectors,
        IReadOnlyList<IReadOnlyList<string>> eyesOnlySets,
        bool ukPrefix,
        SelectorCatalog catalog)
    {
        Level = level;
        Selectors = selectors;
        EyesOnlySets = eyesOnlySets;
        UkPrefix = ukPrefix;
        Label = ProtectiveMarking.FormatLabel(
            ukPrefix ? ProtectiveMarking.UkPrefix : null, level, selectors, eyesOnlySets, catalog);
        LevelName = ProtectiveMarking.LevelName(level);
    }

    /// <summary>The <b>maximum</b> level over every contributing source — the whole of the
    /// aggregation rule for the part of a marking that is totally ordered.</summary>
    public ClassificationLevel Level { get; }

    /// <summary>The union of every source's selectors, distinct, in canonical (ordinal)
    /// order; the label renders them in the catalog's configured order. See the class doc
    /// for why union is the honest reading here and not for the caveat.</summary>
    public IReadOnlyList<SelectorValue> Selectors { get; }

    /// <summary>
    /// The distinct eyes-only sets carried by the sources, each one canonical, the list
    /// itself ordered deterministically so the same inputs in any retrieval order render
    /// byte-identically. Two sources with the same set collapse to one entry; a source
    /// with no caveat contributes nothing.
    ///
    /// <para><b>Empty means, and can only mean, "no source carried a caveat".</b> See the
    /// class doc for why any other route to empty would be a silent widening.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> EyesOnlySets { get; }

    /// <summary>
    /// The prefix toggle (§21.12), carried through only when <b>every</b> source carries
    /// the UK prefix. Any disagreement — a source with none, or a legacy source with
    /// some other prefix — drops to false, which renders the bare level.
    ///
    /// <para><b>Why drop rather than pick.</b> The prefix gates nothing (§21.12), so the
    /// only way it can be wrong is by misrepresenting. Picking a winner — most common,
    /// first seen, highest-classified source — would assert a national qualifier no single
    /// source asserted and would make the label depend on retrieval order, which is not a
    /// property of the content. Dropping it is the same answer
    /// <see cref="ProtectiveMarking.FailClosed"/> already gives for "we do not know what
    /// this marking said": no prefix, which is a legal and visibly plainer state, not a
    /// guess. Note this also means one FailClosed source (prefixless by construction)
    /// strips the prefix off the whole label — correctly, since something is wrong.</para>
    /// </summary>
    public bool UkPrefix { get; }

    /// <summary>
    /// The rendered string, and the <b>only</b> thing a client should display. Built by
    /// <see cref="ProtectiveMarking.FormatLabel"/> — the same implementation
    /// <c>marking.label</c> uses for a single page — so an aggregate and a page marking
    /// can never render the same thing two ways (§21.1). The SPA and an MCP client compose
    /// nothing.
    /// </summary>
    public string Label { get; }

    /// <summary>The level alone in its UK written form, for a one-word badge — the
    /// <c>PageMarkingView.LevelName</c> field's twin, and understating in exactly the same
    /// way if rendered where <see cref="Label"/> belongs.</summary>
    public string LevelName { get; }

    /// <summary>
    /// Fold a set of contributing markings into one label, or null when there are no
    /// sources (see the class doc). The catalog decides only the selectors' display order.
    ///
    /// <para>The caller decides what "contributing" means, and for Ask the answer is
    /// deliberately broad: <b>everything that entered the model context</b>, not merely
    /// what earned a citation. Retrieved content that shaped an answer without being cited
    /// still shaped it, and a marking that only covered the footnotes would be a marking
    /// that can be defeated by a model choosing not to cite.</para>
    /// </summary>
    public static AggregateMarkingLabel? Of(IEnumerable<ProtectiveMarking> sources, SelectorCatalog catalog)
    {
        var contributors = sources as IReadOnlyList<ProtectiveMarking> ?? sources.ToArray();
        if (contributors.Count == 0)
        {
            return null;
        }

        var level = contributors.Max(m => m.Level);

        // The union, in canonical order: a property of the content, not of retrieval rank.
        var selectors = contributors
            .SelectMany(m => m.Selectors)
            .Distinct()
            .OrderBy(s => s, SelectorValue.CanonicalOrder)
            .ToArray();

        // Distinct sets, listed. Every marking is canonical by construction, so equal
        // sets are sequence-equal, and ordering by the rendered form makes the list order
        // a property of the content rather than of retrieval rank.
        var eyesOnlySets = contributors
            .Where(m => m.HasEyesOnly)
            .Select(m => m.EyesOnly)
            .Distinct(CountrySetComparer.Instance)
            .OrderBy(set => string.Join("/", set), StringComparer.Ordinal)
            .ToArray();

        // Unanimity or nothing: every source carries the UK prefix, or the label carries
        // none. A source with no prefix, and a legacy source with some other prefix,
        // both count as disagreement.
        var ukPrefix = contributors.All(m => string.Equals(m.Prefix, ProtectiveMarking.UkPrefix, StringComparison.Ordinal));

        return new AggregateMarkingLabel(level, selectors, eyesOnlySets, ukPrefix, catalog);
    }

    /// <summary>Sequence equality over canonical country sets, so <c>{UK, US}</c> from two
    /// different pages collapses to one entry in the label.</summary>
    private sealed class CountrySetComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static CountrySetComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y) =>
            x is null ? y is null : y is not null && x.SequenceEqual(y, StringComparer.Ordinal);

        public int GetHashCode(IReadOnlyList<string> obj)
        {
            var hash = new HashCode();
            foreach (var country in obj)
            {
                hash.Add(country, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>Records compare <see cref="EyesOnlySets"/> and <see cref="Selectors"/> by
    /// reference; two labels that render the same string must be equal, so both members
    /// are overridden — the same reason <see cref="ProtectiveMarking"/> overrides them.</summary>
    public bool Equals(AggregateMarkingLabel? other) =>
        other is not null
        && Level == other.Level
        && UkPrefix == other.UkPrefix
        && Selectors.SequenceEqual(other.Selectors)
        && EyesOnlySets.SequenceEqual(other.EyesOnlySets, CountrySetComparer.Instance);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Level);
        hash.Add(UkPrefix);
        foreach (var selector in Selectors)
        {
            hash.Add(selector);
        }

        foreach (var set in EyesOnlySets)
        {
            hash.Add(CountrySetComparer.Instance.GetHashCode(set));
        }

        return hash.ToHashCode();
    }
}
