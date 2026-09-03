using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21: one page's protective marking — a <see cref="ClassificationLevel"/>,
/// an optional set of <b>additional selectors</b> (§21.15), an optional <b>eyes-only</b>
/// national caveat (§21.4), and an optional presentational <b>prefix</b> (§21.12).
/// Written <c>&lt;PREFIX&gt; &lt;CLASSIFICATION&gt; &lt;SELECTORS&gt; &lt;CAVEAT&gt;</c>:
/// <c>UK SECRET APPLE NORTH AUS/NZ EYES ONLY</c>.
///
/// <para><b>Three of those four gate access; the prefix does not.</b> The prefix is
/// presentational — the national qualifier UK markings are conventionally written with
/// (<c>UK SECRET</c>) — and it is deliberately outside the gate: <see cref="MarkingGate"/>
/// does not read it, no denial reason mentions it, and no verdict depends on it. It
/// lives on this type only because this type owns the canonical display string. If you
/// are here to "finish" the prefix by giving it access semantics: don't. There is
/// nothing to compare it against, and §21.12 says why.</para>
///
/// <para><b>This is a value object, not the database row.</b> The row is
/// <c>RocketWiki.Core.Entities.PageMarking</c> (plus its country and selector child
/// rows); this is what the rule engine compares against, what the sync payload
/// serializes, and what the display string is built from. Keeping the comparison type
/// separate from the entity is what lets <see cref="EffectivePermissionCalculator"/> stay
/// in Core with no EF dependency, and what lets a caller that never loaded an entity (a
/// search candidate projected straight from SQL) still be gated identically.</para>
///
/// <para><b>Canonical form.</b> Country values are stored and compared upper-cased and
/// sorted ordinally, with duplicates and blanks removed; selectors likewise, one per
/// category, ordered by category. Canonicalizing in ONE place means the stored rows, the
/// display string, the audit <c>DetailsJson</c>, and the sync payload all agree
/// byte-for-byte, and a set that round-trips through sync comes back identical rather
/// than merely equivalent. Construct only through <see cref="Create(ClassificationLevel, IEnumerable{string}?, IEnumerable{SelectorValue}?, string?)"/>
/// — the constructor is private precisely so a non-canonical instance cannot exist.</para>
///
/// <para><b>The vocabularies are fixed, and this type does not filter against them.</b>
/// The caveat draws from <see cref="NationalCaveatVocabulary"/> (five tokens, hard-coded
/// on both sides — §21.4 explains why that is now safe) and the selectors from the
/// instance's configured <see cref="SelectorCatalog"/>. Neither is consulted here: a
/// legacy row carrying <c>GB</c>, or a bundle carrying a selector this instance has not
/// configured, must build a marking that reads back as the thing that is enforced — and
/// what is enforced is "matches nobody". The mutation refuses to <i>write</i> an unknown
/// token; this type refuses to <i>lose</i> one.</para>
/// </summary>
public sealed record ProtectiveMarking
{
    private ProtectiveMarking(
        ClassificationLevel level, IReadOnlyList<string> eyesOnly, IReadOnlyList<SelectorValue> selectors, string? prefix)
    {
        Level = level;
        EyesOnly = eyesOnly;
        Selectors = selectors;
        Prefix = prefix;
    }

    public ClassificationLevel Level { get; }

    /// <summary>
    /// design.md §21.15: the additional selectors, canonical (each
    /// <see cref="SelectorValue"/> is upper-cased and trimmed by construction), <b>at most
    /// one per category</b>, sorted ordinally by category. Empty means none — the page is
    /// limited by its level and caveat alone. Every selector present must be both
    /// eligible-for and granted-to a principal (<see cref="SelectorGate"/>) before the
    /// page is readable.
    ///
    /// <para><b>Stored order is ordinal; displayed order is the catalog's.</b> This value
    /// object cannot know the instance's configured category order — an entity's
    /// <c>ToMarking()</c> has no catalog — so equality, the audit details and the sync
    /// payload use the ordinal canonical order, and <see cref="FormatLabel"/> re-orders
    /// for display. Two markings that mean the same thing are therefore equal regardless
    /// of the order their selectors were supplied in, which is the property the sync
    /// round-trip depends on.</para>
    /// </summary>
    public IReadOnlyList<SelectorValue> Selectors { get; }

    public bool HasSelectors => Selectors.Count > 0;

    /// <summary>
    /// The national prefix, canonical (upper-cased, trimmed) — <c>UK</c> by default.
    /// <b>Null means no prefix</b>, which is legal: some content legitimately carries
    /// none, so it must be clearable, and null renders the bare level with no leading
    /// space.
    ///
    /// <para><b>Presentational only.</b> Nothing in <see cref="MarkingGate"/> reads
    /// this property, and nothing should: a prefix is a national qualifier on how the
    /// marking is written, not a claim about who may read it. Pinned by test — see
    /// <c>ClearanceGateTests</c>' and <c>MarkingGateTests</c>' prefix-invariance cases.</para>
    ///
    /// <para>Through the product it is a <b>toggle</b>: the mutation writes
    /// <see cref="UkPrefix"/> or null, never free text (§21.12). The type keeps a string
    /// rather than a bool so a row written before the toggle existed, or a bundle from an
    /// older instance, renders verbatim rather than being re-interpreted.</para>
    /// </summary>
    public string? Prefix { get; }

    public bool HasPrefix => !string.IsNullOrEmpty(Prefix);

    /// <summary>
    /// The prefix a marking gets when nobody has said otherwise. UK Government markings
    /// are conventionally written <c>UK OFFICIAL</c>, <c>UK SECRET</c>, so this instance
    /// defaults to it — new markings, inherited markings, and (via its own migration)
    /// every row that predated the prefix.
    /// </summary>
    public const string DefaultPrefix = "UK";

    /// <summary>
    /// The prefix toggle's "on" value (design.md §21.12): the mutation offers a UK prefix
    /// or none, never free text, so the only prefix a marking written through the product
    /// can carry is this one. The value object keeps a string rather than a bool so a
    /// legacy row or a bundle from an older instance renders verbatim rather than being
    /// re-interpreted (§21.10).
    /// </summary>
    public const string UkPrefix = DefaultPrefix;

    /// <summary>
    /// The eyes-only country set, canonical (upper-case, ordinal-sorted, distinct).
    /// <b>Empty means no caveat</b> — the page is limited by its level and selectors
    /// alone. A non-empty set means the principal must hold at least one nationality
    /// value in it (design.md §21.4). The tokens a mutation may write are
    /// <see cref="NationalCaveatVocabulary"/>'s five; a token outside it can only be here
    /// from legacy data or a bundle, and matches nobody.
    /// </summary>
    public IReadOnlyList<string> EyesOnly { get; }

    public bool HasEyesOnly => EyesOnly.Count > 0;

    /// <summary>
    /// The marking a page gets when nobody has said otherwise: <c>UK OFFICIAL</c>, no
    /// selectors, no caveat. New root pages take this, and it is what the migrations
    /// backfill every pre-existing page to (design.md §21 — read the risk note there
    /// before assuming the OFFICIAL half is the safe choice; it is the pragmatic one).
    /// </summary>
    public static ProtectiveMarking Baseline { get; } = new(ClassificationLevel.Official, [], [], DefaultPrefix);

    /// <summary>
    /// What the read path uses for a page whose <c>PageMarking</c> row is missing.
    /// Every page is supposed to have exactly one row — creation writes it, import
    /// writes it, the migration backfilled it — so a missing row means a code path
    /// forgot, and the belt-and-braces answer to "a bug lost this page's marking" is
    /// the most restrictive LEVEL in the scheme, not the least (design.md §21).
    ///
    /// <para>The eyes-only set and the selectors are empty here on purpose: TOP SECRET
    /// alone already denies all but the highest-cleared principals, and inventing a
    /// sentinel country or selector would put a token into enforcement that nobody
    /// configured. A missing row can never be <i>less</i> restrictive than any real
    /// marking on selectors either, because a real marking's selectors only subtract
    /// further.</para>
    ///
    /// <para>And <b>no prefix</b>, unlike <see cref="Baseline"/>. This state means "this
    /// page's marking is missing and we do not know what it said", so asserting a
    /// national qualifier on its behalf would be inventing a fact. It renders as a bare
    /// <c>TOP SECRET</c>, which is also a quiet visual signal that something is wrong —
    /// every marking the app actually writes carries a prefix.</para>
    /// </summary>
    public static ProtectiveMarking FailClosed { get; } = new(ClassificationLevel.TopSecret, [], [], null);

    /// <summary>
    /// The only way to build one. Canonicalizes the country set: trims, drops blanks,
    /// upper-cases with the invariant culture, de-duplicates ordinally, and sorts
    /// ordinally.
    ///
    /// <para><b>A level outside the four-member ladder becomes TOP SECRET.</b> The
    /// column is a tinyint, so a hand-edited row, a botched restore, or a future
    /// migration bug can present a value the enum does not define — and the two ways
    /// that could go wrong are not symmetric. A value ABOVE the ladder would deny
    /// everyone (harmless but noisy); a value BELOW it — <c>0</c>, which is what an
    /// uninitialized tinyint is — would compare as less than every clearance and make
    /// the page readable by <i>everybody</i>. That is a silent bypass of the whole
    /// control, so an undefined level is normalized to the top of the scheme here,
    /// where every marking is built, rather than being trusted at each comparison.
    /// Fail closed, §6.3's doctrine applied to a corrupt value instead of a corrupt
    /// rule.</para>
    ///
    /// <para><paramref name="selectors"/> are the page's additional selectors (design.md
    /// §21.15): canonicalized (trimmed, upper-cased), blanks dropped, exact duplicates
    /// collapsed and the result sorted ordinally by category; null or empty means none.
    /// <b>Two different values for one category throw <see cref="ArgumentException"/></b>:
    /// the value object must not be constructible in an invalid state, so the services
    /// validate first and answer with a <c>ValidationError</c>, and the sync importer
    /// treats it as an unparseable marking (which fails closed — §21.10). Nothing here
    /// consults the catalog: a selector this instance does not know is kept verbatim and
    /// matches nobody, the same posture the caveat takes toward a legacy country token.
    /// One factory, four parts, on purpose: a shorter overload that took no selectors
    /// existed while the selectors landed and was deleted once every caller could state
    /// them, so that no call site can build a marking while forgetting they exist.</para>
    ///
    /// <para>The <paramref name="prefix"/> is trimmed and upper-cased; null, empty or
    /// whitespace all collapse to null, which is the legal "no prefix" state. It gets
    /// none of the fail-closed treatment the level gets, because it carries no access
    /// weight to fail closed <i>on</i>.</para>
    /// </summary>
    public static ProtectiveMarking Create(
        ClassificationLevel level, IEnumerable<string>? eyesOnly, IEnumerable<SelectorValue>? selectors = null,
        string? prefix = DefaultPrefix) =>
        new(
            Enum.IsDefined(level) ? level : ClassificationLevel.TopSecret,
            Canonicalize(eyesOnly),
            CanonicalizeSelectors(selectors),
            CanonicalizePrefix(prefix));

    /// <summary>
    /// The canonical form of a prefix: trimmed and upper-cased, or null when there is
    /// nothing left. Same normalize-on-write discipline as the country values, and for
    /// the same reason — the stored row, the label, the audit details and the sync
    /// payload must be byte-identical rather than merely equivalent.
    /// </summary>
    public static string? CanonicalizePrefix(string? prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim().ToUpperInvariant();

    /// <summary>
    /// The canonical form of one country value. <b>Upper-case invariant</b> is the
    /// canonical form on both sides of every comparison — the marking's stored rows and
    /// the principal's nationality claim values alike.
    ///
    /// <para>This is a deliberate, documented departure from §6.3's "matching is exact
    /// (ordinal), no case folding" rule, and it is confined to marking comparisons. §6.3
    /// keeps rule-expression matching ordinal because an admin hand-typing a group name
    /// should not have a typo silently forgiven. Here the two sides come from different
    /// systems that were never guaranteed to agree on case — the marking's values come
    /// from a fixed vocabulary, the principal's from an OIDC claim mapper — and a case
    /// mismatch would deny every legitimate reader while looking perfectly correct.
    /// Failing closed on a casing difference is not security, it is an outage. The rule
    /// engine's <c>attr</c> conditions are untouched.</para>
    /// </summary>
    public static string CanonicalizeCountry(string value) => value.Trim().ToUpperInvariant();

    private static IReadOnlyList<string> Canonicalize(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(CanonicalizeCountry)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The canonical selector list: blanks dropped, exact duplicates collapsed, ordinal
    /// order by category then value. Throws when one category would carry two values —
    /// see <see cref="Create(ClassificationLevel, IEnumerable{string}?, IEnumerable{SelectorValue}?, string?)"/>.
    /// </summary>
    public static IReadOnlyList<SelectorValue> CanonicalizeSelectors(IEnumerable<SelectorValue>? selectors)
    {
        if (selectors is null)
        {
            return [];
        }

        var canonical = selectors
            .Where(s => s is not null && s.Category.Length > 0 && s.Value.Length > 0)
            .Distinct()
            .OrderBy(s => s, SelectorValue.CanonicalOrder)
            .ToArray();

        for (var i = 1; i < canonical.Length; i++)
        {
            if (string.Equals(canonical[i - 1].Category, canonical[i].Category, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Selector category '{canonical[i].Category}' may carry at most one value on a marking; " +
                    $"got '{canonical[i - 1].Value}' and '{canonical[i].Value}'.",
                    nameof(selectors));
            }
        }

        return canonical;
    }

    /// <summary>
    /// <b>The single source of the display string</b>, server-side, exposed on the
    /// GraphQL <c>PageMarking.label</c> field so the SPA renders exactly what an audit
    /// reviewer and an MCP client see. Two renderings of one marking that disagree is a
    /// compliance problem, not a cosmetic one, so neither side invents its own.
    ///
    /// <para>Takes the instance's <see cref="SelectorCatalog"/> for one reason only: the
    /// selectors render in <i>configured</i> order (§21.15), and this value object stores
    /// them in ordinal order because it cannot know the configuration. The catalog
    /// changes nothing else about the string — an unknown selector still renders, last —
    /// so a label can never omit something that is enforced.</para>
    ///
    /// <para>The rendering itself lives in <see cref="FormatLabel"/>, which this is a
    /// one-set special case of. That indirection exists so an <i>aggregate</i> label over
    /// several pages (§21.13) renders through the same code rather than composing its own
    /// — the second formatter §21.1 exists to forbid.</para>
    /// </summary>
    public string Format(SelectorCatalog catalog) =>
        FormatLabel(Prefix, Level, Selectors, HasEyesOnly ? [EyesOnly] : [], catalog);

    /// <summary>
    /// <b>THE</b> renderer — the one implementation <see cref="Format"/> delegates to, and
    /// the one an <i>aggregate</i> marking label (§21.13) delegates to as well. It takes
    /// the parts loose rather than a whole <see cref="ProtectiveMarking"/> for exactly
    /// one reason: an aggregate over several sources can carry things a single page's
    /// marking cannot — <b>more than one eyes-only set</b>, and two values of one
    /// selector category — and the alternative (letting the aggregate compose its own
    /// string) is the second formatter §21.1 forbids.
    ///
    /// <para><b>Grammar:</b> <c>[PREFIX ]LEVEL[ SELECTOR …][ A/B EYES ONLY[, C EYES ONLY …]]</c>,
    /// single spaces, no brackets. <c>UK SECRET APPLE NORTH AUS/NZ EYES ONLY</c>.</para>
    ///
    /// <para><b>Selectors</b> render as values only — never the category name — in the
    /// catalog's configured category order, then ordinally by category for categories
    /// the catalog does not know (they still render: what is enforced must be readable),
    /// then ordinally by value within a category. A page carries at most one value per
    /// category; an aggregate may carry several.</para>
    ///
    /// <para><b>Several caveat sets are LISTED, never merged.</b>
    /// <c>UK SECRET AUS/NZ EYES ONLY, US EYES ONLY</c> means "one source was AUS/NZ-only
    /// and another was US-only" — a conjunction, i.e. a reader needs both. Merging them
    /// into <c>AUS/NZ/US EYES ONLY</c> would say the opposite (any one nationality
    /// suffices) and intersecting them would produce an <i>empty</i> set, which in this
    /// format renders as no caveat at all: the least restrictive possible answer from the
    /// two most restrictive inputs. See <c>AggregateMarkingLabel</c> for why that
    /// intersection is the level-0 trap wearing a different hat. The comma is the one
    /// separator that keeps two sets readable as two now that the brackets are gone.</para>
    ///
    /// <para>Zero selectors and zero sets render the bare (prefixed) level; no prefix
    /// means no leading space — a cosmetic gap would make two identical markings compare
    /// unequal as strings (§21.12). A set that is somehow empty contributes nothing
    /// rather than a dangling <c>EYES ONLY</c> — so an empty rendering can only ever mean
    /// "no caveat", which is the property the aggregate's emptiness rule depends on.</para>
    /// </summary>
    public static string FormatLabel(
        string? prefix,
        ClassificationLevel level,
        IReadOnlyList<SelectorValue> selectors,
        IReadOnlyList<IReadOnlyList<string>> caveatSets,
        SelectorCatalog catalog)
    {
        var builder = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(prefix))
        {
            builder.Append(prefix).Append(' ');
        }

        builder.Append(LevelName(level));

        foreach (var selector in selectors
            .OrderBy(s => catalog.DisplayIndex(s.Category))
            .ThenBy(s => s.Category, StringComparer.Ordinal)
            .ThenBy(s => s.Value, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(selector.Value);
        }

        var first = true;
        foreach (var set in caveatSets)
        {
            if (set.Count == 0)
            {
                continue;
            }

            builder.Append(first ? " " : ", ").Append(string.Join("/", set)).Append(" EYES ONLY");
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The UK Government's own written forms, which are not the C# member names:
    /// OFFICIAL-SENSITIVE is hyphenated and TOP SECRET is two words. The GraphQL enum
    /// and the sync wire format use the SCREAMING_SNAKE member names instead
    /// (<c>OFFICIAL_SENSITIVE</c>, <c>TOP_SECRET</c>) — machine identifiers and human
    /// markings are different strings on purpose.
    /// </summary>
    public static string LevelName(ClassificationLevel level) => level switch
    {
        ClassificationLevel.Official => "OFFICIAL",
        ClassificationLevel.OfficialSensitive => "OFFICIAL-SENSITIVE",
        ClassificationLevel.Secret => "SECRET",
        ClassificationLevel.TopSecret => "TOP SECRET",
        // Unreachable through Create, which normalizes an undefined level to TOP SECRET.
        // Answering rather than throwing anyway: these three methods run on read paths,
        // and an exception there would turn a page read into a 500 - which is
        // DISTINGUISHABLE from a not-found, and therefore the §6.7 leak the whole denial
        // design exists to prevent. The most restrictive answer is the safe one.
        _ => "TOP SECRET",
    };

    /// <summary>
    /// The machine name of a level: <c>OFFICIAL</c>, <c>OFFICIAL_SENSITIVE</c>,
    /// <c>SECRET</c>, <c>TOP_SECRET</c>. This is the single spelling used by the
    /// <c>clearance</c> claim (<see cref="ClearanceGate.TryParseLevel"/>), the sync wire
    /// format (§12), the audit <c>DetailsJson</c>, and the GraphQL enum — deliberately
    /// not <c>Level.ToString()</c>, which would emit the C# member spelling
    /// (<c>TopSecret</c>) and quietly make the claim vocabulary and the audit vocabulary
    /// two different things.
    /// </summary>
    public static string LevelWireName(ClassificationLevel level) => level switch
    {
        ClassificationLevel.Official => "OFFICIAL",
        ClassificationLevel.OfficialSensitive => "OFFICIAL_SENSITIVE",
        ClassificationLevel.Secret => "SECRET",
        ClassificationLevel.TopSecret => "TOP_SECRET",
        _ => "TOP_SECRET", // see LevelName - never throw on a read path
    };

    /// <summary>
    /// The bounded token used in denial reasons and audit details:
    /// <c>official</c> / <c>official_sensitive</c> / <c>secret</c> / <c>top_secret</c>.
    /// </summary>
    public static string LevelToken(ClassificationLevel level) => level switch
    {
        ClassificationLevel.Official => "official",
        ClassificationLevel.OfficialSensitive => "official_sensitive",
        ClassificationLevel.Secret => "secret",
        ClassificationLevel.TopSecret => "top_secret",
        _ => "top_secret", // see LevelName - never throw on a read path
    };

    /// <summary>
    /// design.md §21.6: is moving <paramref name="from"/> to <paramref name="to"/> a
    /// <b>downgrade</b> — a change that makes the page readable by someone it was not
    /// readable by before? Downgrading is permitted, but it is the operationally risky
    /// direction, so it gets its own audit action (<c>page.marking.downgrade</c>) and a
    /// reviewer can find every one of them with a single query.
    ///
    /// <para>Three ways to widen the audience, and all count:</para>
    /// <list type="number">
    /// <item>the level drops; or</item>
    /// <item>the eyes-only caveat is <i>relaxed</i> — cleared entirely, or extended to a
    /// country that was not previously admitted. Note that swapping <c>{UK}</c> for
    /// <c>{US}</c> counts as a downgrade even though it also excludes UK: somebody who
    /// could not read this page yesterday can read it today, which is the fact a
    /// reviewer is looking for. Erring toward "call it a downgrade" is the safe error
    /// here — the cost is an extra row in a reviewer's result set, and the cost of the
    /// opposite error is a widening nobody sees; or</item>
    /// <item>a selector is <i>removed</i> — cleared, or swapped for another value in the
    /// same category (which removes the old one). A selector only ever subtracts readers
    /// (§21.15), so taking one away admits everyone it was excluding; swapping
    /// <c>APPLE</c> for <c>BANANA</c> admits every BANANA holder who lacked APPLE.
    /// Adding a selector never widens.</item>
    /// </list>
    ///
    /// <para><b>The prefix is deliberately not consulted.</b> A downgrade is defined as
    /// "somebody who could not read this page yesterday can read it today", and the
    /// prefix cannot move that line in either direction — it is not read by the gate at
    /// all. Changing <c>UK SECRET</c> to <c>SECRET</c> is therefore an ordinary
    /// <c>page.marking.set</c>, not a downgrade, and the audit row still records the
    /// before-and-after prefix so a reviewer can see exactly what happened. Counting it
    /// as a downgrade would dilute the one query that exists to find real
    /// widenings.</para>
    /// </summary>
    public static bool IsDowngrade(ProtectiveMarking from, ProtectiveMarking to)
    {
        if (to.Level < from.Level)
        {
            return true;
        }

        if (from.Selectors.Any(s => !to.Selectors.Contains(s)))
        {
            return true;
        }

        if (!from.HasEyesOnly)
        {
            // No caveat before: any caveat now can only narrow the audience.
            return false;
        }

        return !to.HasEyesOnly || to.EyesOnly.Any(c => !from.EyesOnly.Contains(c, StringComparer.Ordinal));
    }

    /// <summary>
    /// Records equality would compare <see cref="EyesOnly"/> and <see cref="Selectors"/>
    /// by reference; two markings with the same canonical countries and selectors must be
    /// equal, so both members are overridden. The prefix participates — two markings that
    /// render differently are different markings, even though they gate identically.
    /// </summary>
    public bool Equals(ProtectiveMarking? other) =>
        other is not null
        && Level == other.Level
        && string.Equals(Prefix, other.Prefix, StringComparison.Ordinal)
        && EyesOnly.SequenceEqual(other.EyesOnly, StringComparer.Ordinal)
        && Selectors.SequenceEqual(other.Selectors);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Level);
        hash.Add(Prefix, StringComparer.Ordinal);
        foreach (var country in EyesOnly)
        {
            hash.Add(country, StringComparer.Ordinal);
        }

        foreach (var selector in Selectors)
        {
            hash.Add(selector);
        }

        return hash.ToHashCode();
    }
}
