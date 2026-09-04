using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;

namespace RocketWiki.Core.Entities;

/// <summary>
/// data-model.md / design.md §21: one page's protective marking. <b>1:1 with
/// <see cref="Page"/>, PK = <see cref="PageId"/></b> — the primary key is the page id
/// itself, so a page physically cannot carry two markings and "which one wins" is not a
/// question anyone can ask.
///
/// <para>Every page has exactly one of these rows. Creation writes one (inheriting the
/// parent's, or OFFICIAL at the root), sync import writes one, and the
/// <c>AddPageMarkings</c> migration backfilled every page that existed before the
/// feature. A page found without one is treated as unavailable by the read path
/// (<see cref="ProtectiveMarking.FailClosed"/>, readable by nobody) — belt and braces
/// against a future code path that forgets, never a licence for one to exist.</para>
///
/// <para>A row can also SAY it is unavailable — <see cref="IsUnavailable"/> — which is how
/// "we do not know this page's marking" survives the persistence boundary. See that
/// property for the fail-open it closes.</para>
/// </summary>
public class PageMarking
{
    /// <summary>PK and FK in one — see the class doc.</summary>
    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    public ClassificationLevel Level { get; set; } = ClassificationLevel.Official;

    /// <summary>
    /// True when this page's marking is <b>unknown</b>: the row exists (every page has
    /// one) but nobody has stated what the page is marked. The sync importer writes it
    /// for a page arriving from a bundle that carries no marking (a format-1/2 bundle,
    /// or a malformed one — design.md §21.10), and <see cref="ToMarking"/> then returns
    /// <see cref="ProtectiveMarking.FailClosed"/>, which denies everyone.
    ///
    /// <para><b>Why a column, and not a sentinel level.</b> The importer used to persist
    /// that case as a plain TOP SECRET row, and TOP SECRET denied all but the
    /// highest-cleared — so "unknown" and "TOP SECRET" could share a representation.
    /// The level gates nothing now (§21.12), so a bare TOP SECRET row is readable by
    /// everyone with space access, and an unknown marking stored that way would be a
    /// silent fail-open on every pre-marking bundle. The state therefore has to be one
    /// the database can hold in its own right, and it is never inferred from the level:
    /// a real page legitimately marked TOP SECRET reads <c>false</c> here.</para>
    ///
    /// <para><b>Stating a marking clears it.</b> Every writer that persists a
    /// <see cref="ProtectiveMarking"/> copies its <see cref="ProtectiveMarking.IsUnavailable"/>
    /// onto this column, and the only marking that carries <c>true</c> is
    /// <see cref="ProtectiveMarking.FailClosed"/> itself — so a declared marking arriving
    /// through sync, or set through <c>setPageMarking</c>, replaces "unknown" with a
    /// known value, and nothing that builds a marking through <c>ProtectiveMarking.Create</c>
    /// can ever set it.</para>
    /// </summary>
    public bool IsUnavailable { get; set; }

    /// <summary>
    /// The national prefix a UK marking is conventionally written with — <c>UK</c> by
    /// default, giving <c>UK SECRET</c>. Canonical (upper-cased, trimmed);
    /// <b>null means no prefix</b>, which is legal and must stay clearable.
    ///
    /// <para><b>Presentational only, and deliberately so</b> (design.md §21.12). It is
    /// not read by <c>ClearanceGate</c>, never appears in a denial reason, and changes no
    /// verdict. It is stored beside the level because it is part of how this page's
    /// marking is <i>written</i>, not part of what it <i>permits</i>.</para>
    /// </summary>
    public string? Prefix { get; set; } = ProtectiveMarking.DefaultPrefix;

    /// <summary>
    /// The eyes-only country set (design.md §21.4). Empty means no caveat. Values are
    /// canonical (upper-case, see <see cref="ProtectiveMarking.CanonicalizeCountry"/>)
    /// and, for anything written through the product, drawn from the fixed
    /// <c>NationalCaveatVocabulary</c>. A row can still hold a token outside it — legacy
    /// data, or a bundle from an older instance — and such a token matches nobody.
    /// </summary>
    public ICollection<PageMarkingCountry> Countries { get; set; } = new List<PageMarkingCountry>();

    /// <summary>
    /// The additional selectors (design.md §21.15), at most one per category — a
    /// database fact via <see cref="PageMarkingSelector"/>'s primary key. Empty means
    /// none. A row can hold a category or value this instance no longer (or never)
    /// configured; the gate reads it as unknown, which admits nobody.
    /// </summary>
    public ICollection<PageMarkingSelector> Selectors { get; set; } = new List<PageMarkingSelector>();

    public DateTime SetAtUtc { get; set; }

    /// <summary>
    /// Null when the row was applied by a sync import rather than written by a local
    /// user — the payload carries no actor and a replica is read-only to users anyway.
    /// The same "system action, no user" shape <c>PageProperty.UpdatedByUserId</c> and
    /// <c>AuditEvent.UserId</c> already have.
    /// </summary>
    public Guid? SetByUserId { get; set; }

    public User? SetByUser { get; set; }

    /// <summary>
    /// The comparison value the rule engine actually uses. Canonicalizes on the way out
    /// so a row hand-edited into a non-canonical state still compares correctly. The
    /// caller must have loaded <see cref="Countries"/> and <see cref="Selectors"/>
    /// (every marking query includes both); a row read without its children would
    /// compare as a LESS restrictive marking than it is, which is the one direction this
    /// type must never err in.
    ///
    /// <para>An <see cref="IsUnavailable"/> row is <see cref="ProtectiveMarking.FailClosed"/>
    /// — the one instance that denies everyone — whatever its other columns say. The
    /// flag is read, never inferred: <c>ProtectiveMarking.Create</c> cannot build an
    /// unavailable marking, so the level, countries and selectors of such a row are
    /// deliberately not consulted.</para>
    /// </summary>
    public ProtectiveMarking ToMarking() =>
        IsUnavailable
            ? ProtectiveMarking.FailClosed
            : ProtectiveMarking.Create(
                Level,
                Countries.Select(c => c.CountryValue),
                Selectors.Select(s => SelectorValue.Canonical(s.Category, s.Value)),
                Prefix);
}

/// <summary>
/// One country in a page's eyes-only set. A child table rather than a delimited column,
/// deliberately: this is enforcement-critical data, so it should be queryable as data —
/// "which pages are releasable to X" is a report someone will want, and a
/// <c>LIKE '%GB%'</c> over a packed string is exactly the kind of near-miss that quietly
/// answers the wrong question about an access control.
///
/// <para>Composite PK <c>(PageId, CountryValue)</c>, the same shape <c>PageLabel</c> and
/// <c>PageProperty</c> use: a country can appear at most once per page, so applying a set
/// is idempotent and there is no ordering question between two rows for the same
/// country. Canonical order for display and serialization is derived (ordinal sort), not
/// stored — a stored sort column would be another thing that can disagree.</para>
/// </summary>
public class PageMarkingCountry
{
    public Guid PageId { get; set; }

    public PageMarking? Marking { get; set; }

    /// <summary>Canonical (trimmed, upper-cased). See <see cref="ProtectiveMarking.CanonicalizeCountry"/>.</summary>
    public string CountryValue { get; set; } = string.Empty;
}
