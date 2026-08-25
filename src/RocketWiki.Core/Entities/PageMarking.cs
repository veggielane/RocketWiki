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
/// feature. A page found without one is treated as TOP SECRET by the read path
/// (<see cref="ProtectiveMarking.FailClosed"/>) — belt and braces against a future code
/// path that forgets, never a licence for one to exist.</para>
/// </summary>
public class PageMarking
{
    /// <summary>PK and FK in one — see the class doc.</summary>
    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    public ClassificationLevel Level { get; set; } = ClassificationLevel.Official;

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
    /// The eyes-only country set (design.md §21). Empty means no caveat. Values are
    /// canonical (upper-case, see <see cref="ProtectiveMarking.CanonicalizeCountry"/>)
    /// and drawn from the registered <c>nationality</c> attribute's allowed values, NOT
    /// from an ISO list — see <c>PageMarkingService</c> for why that distinction is
    /// load-bearing.
    /// </summary>
    public ICollection<PageMarkingCountry> Countries { get; set; } = new List<PageMarkingCountry>();

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
    /// so a row hand-edited into a non-canonical state still compares correctly.
    /// </summary>
    public ProtectiveMarking ToMarking() =>
        ProtectiveMarking.Create(Level, Countries.Select(c => c.CountryValue), Prefix);
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
