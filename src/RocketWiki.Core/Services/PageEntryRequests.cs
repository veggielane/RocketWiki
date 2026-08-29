using RocketWiki.Core.Access;

namespace RocketWiki.Core.Services;

/// <summary>
/// An entry as a caller sees it. `Marking` is carried out rather than re-derived, for the
/// same reason <c>PageTreeNode</c> carries the marking its walk gated on: a second lookup
/// could read a different row than the one the gate consulted, and a view that displayed a
/// marking other than the one it enforced would be the wrong kind of wrong.
/// </summary>
public sealed record PageEntryView(
    Guid Id,
    Guid PageId,
    string Collection,
    string Data,
    int Version,
    ProtectiveMarking Marking,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

/// <summary>
/// <paramref name="Marking"/> null means "inherit the page's", which is the ordinary case
/// — a form submitter should not have to think about classification to file a record.
/// Stating one explicitly is for the cases where an entry genuinely differs.
/// </summary>
public sealed record CreatePageEntryRequest(
    Guid PageId,
    string Collection,
    string Data,
    ProtectiveMarking? Marking = null);

/// <summary>
/// <paramref name="ExpectedVersion"/> is the compare-and-set token. Required rather than
/// optional: forms have concurrent submitters, and an update path that silently accepts
/// "whatever is there now" is a lost-update bug that only appears under load.
///
/// <para><paramref name="Marking"/> null here means "leave the marking alone", NOT
/// "clear it" — an entry has no unmarked state to clear to. That asymmetry with the
/// create request is deliberate and is why they are separate types.</para>
/// </summary>
public sealed record UpdatePageEntryRequest(
    Guid EntryId,
    int ExpectedVersion,
    string Data,
    ProtectiveMarking? Marking = null);

public sealed record DeletePageEntryRequest(Guid EntryId, int ExpectedVersion);
