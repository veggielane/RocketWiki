namespace RocketWiki.Core.Entities;

/// <summary>
/// One contributor of a co-edited revision (design.md §8 CRDT co-editing, milestone
/// decision): a user whose live edit-session updates are part of the content that
/// <see cref="PageRevision"/> snapshots. Composite PK (PageRevisionId, UserId);
/// immutable and append-only, exactly like the revision it belongs to.
///
/// A join table rather than a JSON column on PageRevision, deliberately: attribution
/// is §7-adjacent ("who wrote what" is a compliance question here), so it must be
/// queryable per user ("every revision user X contributed to") with referential
/// integrity to real User rows — a JSON array would give neither. The FK targets
/// PageRevision's own PK (Id) rather than the (PageId, RevisionNumber) unique index:
/// every FK in this schema targets a primary key, and an alternate-key FK would buy
/// nothing over joining through the revision row.
///
/// <see cref="PageRevision.AuthorUserId"/> is NOT replaced by this: the author stays
/// "who pressed save" (the acting user of the updatePageContent mutation, who the
/// audit row and outbox entry already name); contributor rows record everyone whose
/// session updates fed the revision — including the author, when they typed too.
/// Rows exist only for revisions saved out of a live edit session; a solo,
/// non-session save has none. UserIds here come exclusively from the server's own
/// edit-session registry (membership established via canEdit at join) — never from
/// client input, which is what keeps forged attribution impossible (design.md §7).
/// </summary>
public class PageRevisionContributor
{
    public Guid PageRevisionId { get; set; }
    public PageRevision? PageRevision { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }
}
