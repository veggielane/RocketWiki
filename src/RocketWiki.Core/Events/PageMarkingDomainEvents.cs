using RocketWiki.Core.Access;

namespace RocketWiki.Core.Events;

/// <summary>
/// A page's protective marking was set (design.md §21). <b>One event type, two audit
/// actions</b>: <see cref="DomainEventAuditMapper"/> writes
/// <c>page.marking.downgrade</c> when <see cref="IsDowngrade"/> holds and
/// <c>page.marking.set</c> otherwise. Splitting it into two event types was considered
/// and rejected — the two would carry identical payloads, sync identically, and differ
/// only in a predicate over their own fields, so deriving the action from the event keeps
/// the "what counts as a downgrade" question in exactly one place
/// (<see cref="ProtectiveMarking.IsDowngrade"/>) instead of two call sites that can drift.
///
/// <para>Carries the marking <b>before and after</b>. The before-state is not decoration:
/// §21 makes downgrading auditable-as-such, and a reviewer asking "what was it before
/// this was relaxed" has no other source — markings are a mutable single row, so like
/// access rules (§7) the audit log is the only history there is.</para>
///
/// <para>Both <c>SpaceId</c> and <c>SpaceKey</c> are present for the same reason
/// <see cref="PagePropertySetEvent"/> carries both: the audit mapper writes the key onto
/// the row, and <c>SyncOutboxWriter</c> looks the tracked Space up by key.</para>
/// </summary>
public sealed record PageMarkingSetEvent(
    Guid PageId,
    Guid SpaceId,
    string SpaceKey,
    Guid? ActorUserId,
    ProtectiveMarking Before,
    ProtectiveMarking After) : IDomainEvent
{
    /// <summary>See <see cref="ProtectiveMarking.IsDowngrade"/> — a change that lets
    /// somebody read the page who could not read it before.</summary>
    public bool IsDowngrade => ProtectiveMarking.IsDowngrade(Before, After);
}
