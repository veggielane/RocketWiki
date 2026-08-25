using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §21: one page's protective marking — a <see cref="ClassificationLevel"/>
/// plus an optional <b>eyes-only</b> set of country values that limits the page to
/// principals holding one of those nationalities.
///
/// <para><b>This is a value object, not the database row.</b> The row is
/// <c>RocketWiki.Core.Entities.PageMarking</c> (plus its country child rows); this is
/// what the rule engine compares against, what the sync payload serializes, and what
/// the display string is built from. Keeping the comparison type separate from the
/// entity is what lets <see cref="EffectivePermissionCalculator"/> stay in Core with no
/// EF dependency, and what lets a caller that never loaded an entity (a search
/// candidate projected straight from SQL) still be gated identically.</para>
///
/// <para><b>Canonical form.</b> Country values are stored and compared upper-cased and
/// sorted ordinally, with duplicates and blanks removed. Canonicalizing in ONE place
/// means the stored rows, the display string, the audit <c>DetailsJson</c>, and the
/// sync payload all agree byte-for-byte, and a set that round-trips through sync comes
/// back identical rather than merely equivalent. Construct only through
/// <see cref="Create"/> — the constructor is private precisely so a non-canonical
/// instance cannot exist.</para>
///
/// <para><b>The country vocabulary is NOT an ISO list.</b> It is whatever the instance
/// registered as the allowed values of the <c>nationality</c> attribute (§6.2). See
/// <see cref="ClearanceGate"/> for why that matters and what breaks if someone
/// "simplifies" it into ISO 3166.</para>
/// </summary>
public sealed record ProtectiveMarking
{
    private ProtectiveMarking(ClassificationLevel level, IReadOnlyList<string> eyesOnly)
    {
        Level = level;
        EyesOnly = eyesOnly;
    }

    public ClassificationLevel Level { get; }

    /// <summary>
    /// The eyes-only country set, canonical (upper-case, ordinal-sorted, distinct).
    /// <b>Empty means no caveat</b> — the page is limited by its level alone. A
    /// non-empty set means the principal must hold at least one nationality value in
    /// it (design.md §21).
    /// </summary>
    public IReadOnlyList<string> EyesOnly { get; }

    public bool HasEyesOnly => EyesOnly.Count > 0;

    /// <summary>
    /// The marking a page gets when nobody has said otherwise: OFFICIAL, no caveat.
    /// New root pages take this, and it is what the migration backfills every
    /// pre-existing page to (design.md §21 — read the risk note there before assuming
    /// that is the safe choice; it is the pragmatic one).
    /// </summary>
    public static ProtectiveMarking Baseline { get; } = new(ClassificationLevel.Official, []);

    /// <summary>
    /// What the read path uses for a page whose <c>PageMarking</c> row is missing.
    /// Every page is supposed to have exactly one row — creation writes it, import
    /// writes it, the migration backfilled it — so a missing row means a code path
    /// forgot, and the belt-and-braces answer to "a bug lost this page's marking" is
    /// the most restrictive LEVEL in the scheme, not the least (design.md §21).
    ///
    /// <para>The eyes-only set is empty here on purpose: the caveat vocabulary is the
    /// instance's own registered nationality values, so there is no country string this
    /// code could invent that would mean "nobody". TOP SECRET alone already denies all
    /// but the highest-cleared principals, and inventing a sentinel country would put a
    /// value into enforcement that no admin ever registered.</para>
    /// </summary>
    public static ProtectiveMarking FailClosed { get; } = new(ClassificationLevel.TopSecret, []);

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
    /// </summary>
    public static ProtectiveMarking Create(ClassificationLevel level, IEnumerable<string>? eyesOnly) =>
        new(Enum.IsDefined(level) ? level : ClassificationLevel.TopSecret, Canonicalize(eyesOnly));

    /// <summary>
    /// The canonical form of one country value. <b>Upper-case invariant</b> is the
    /// canonical form on both sides of every comparison — the marking's stored rows and
    /// the principal's nationality claim values alike.
    ///
    /// <para>This is a deliberate, documented departure from §6.3's "matching is exact
    /// (ordinal), no case folding" rule, and it is confined to this one comparison. §6.3
    /// keeps rule-expression matching ordinal because an admin hand-typing a group name
    /// should not have a typo silently forgiven. Here the two sides come from different
    /// systems that were never guaranteed to agree on case — the marking's values come
    /// from the instance's registered nationality vocabulary, the principal's come from
    /// an OIDC claim mapper — and a case mismatch would deny every legitimate reader
    /// while looking perfectly correct. Failing closed on a casing difference is not
    /// security, it is an outage. The rule engine's <c>attr</c> conditions are
    /// untouched.</para>
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
    /// <b>The single source of the display string</b>, server-side, exposed on the
    /// GraphQL <c>PageMarking.label</c> field so the SPA renders exactly what an audit
    /// reviewer and an MCP client see. Two renderings of one marking that disagree is a
    /// compliance problem, not a cosmetic one, so neither side invents its own.
    ///
    /// <para>Format: the level, then the caveat in square brackets —
    /// <c>SECRET [UK EYES ONLY]</c> for one country, <c>SECRET [UK/US EYES ONLY]</c>
    /// for several, joined in canonical order. The country tokens are the instance's
    /// own registered nationality values verbatim: if this instance registered
    /// <c>GB</c>, it renders <c>[GB EYES ONLY]</c>. Mapping <c>GB</c> to <c>UK</c> for
    /// display was considered and rejected — a marking must read back as the thing that
    /// is actually enforced, and a display-only alias is how "we thought it said UK"
    /// happens.</para>
    /// </summary>
    public string Format() => HasEyesOnly
        ? $"{LevelName(Level)} [{string.Join("/", EyesOnly)} EYES ONLY]"
        : LevelName(Level);

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
    /// design.md §21: is moving <paramref name="from"/> to <paramref name="to"/> a
    /// <b>downgrade</b> — a change that makes the page readable by someone it was not
    /// readable by before? Downgrading is permitted, but it is the operationally risky
    /// direction, so it gets its own audit action (<c>page.marking.downgrade</c>) and a
    /// reviewer can find every one of them with a single query.
    ///
    /// <para>Two ways to widen the audience, and both count:</para>
    /// <list type="number">
    /// <item>the level drops; or</item>
    /// <item>the eyes-only caveat is <i>relaxed</i> — cleared entirely, or extended to a
    /// country that was not previously admitted. Note that swapping <c>{GB}</c> for
    /// <c>{US}</c> counts as a downgrade even though it also excludes GB: somebody who
    /// could not read this page yesterday can read it today, which is the fact a
    /// reviewer is looking for. Erring toward "call it a downgrade" is the safe error
    /// here — the cost is an extra row in a reviewer's result set, and the cost of the
    /// opposite error is a widening nobody sees.</item>
    /// </list>
    /// </summary>
    public static bool IsDowngrade(ProtectiveMarking from, ProtectiveMarking to)
    {
        if (to.Level < from.Level)
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
    /// Records equality would compare <see cref="EyesOnly"/> by reference; two markings
    /// with the same canonical countries must be equal, so both members are overridden.
    /// </summary>
    public bool Equals(ProtectiveMarking? other) =>
        other is not null && Level == other.Level && EyesOnly.SequenceEqual(other.EyesOnly, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Level);
        foreach (var country in EyesOnly)
        {
            hash.Add(country, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
