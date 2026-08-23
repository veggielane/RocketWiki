namespace RocketWiki.Core.Entities;

/// <summary>
/// design.md §9.2 (milestone 7): per-page bookkeeping for the embedding background job —
/// which revision of the page the chunk embeddings in <see cref="PageEmbedding"/> were
/// built from. The job's trigger is a state scan, not an event subscription: a page is
/// "due" when this row is missing or its <see cref="EmbeddedRevisionNumber"/> differs
/// from <c>Page.CurrentRevisionNumber</c>. That catches every write path — GraphQL
/// mutations, revision restores, and sync-imported replica pages (§9.4 requires each
/// instance to embed replicas locally, and the sync CLI is a separate process no
/// in-process event hook would ever see) — and is restart-safe: a crash between save and
/// embed loses nothing, the next scan finds the same delta.
///
/// Like <see cref="PageEmbedding"/>: derived, instance-local, never synced, rebuilt
/// locally (data-model.md "AI"). Rows are written by the system job, not by user
/// mutations, so they carry no audit events (§9.2: embedding jobs are system actions).
/// </summary>
public class PageEmbeddingState
{
    /// <summary>PK and FK to Page — one state row per page.</summary>
    public Guid PageId { get; set; }

    public Page? Page { get; set; }

    /// <summary>
    /// <c>Page.CurrentRevisionNumber</c> at the moment the page's chunks were last
    /// successfully embedded. 0 = never embedded (a failure row created before the
    /// first successful pass).
    /// </summary>
    public int EmbeddedRevisionNumber { get; set; }

    /// <summary>
    /// Consecutive failed embedding attempts since the last success. Non-zero defers the
    /// next retry by the job's failure backoff so an unreachable endpoint degrades to
    /// FTS-only search (§9.2) instead of hammering every poll.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>Set on every attempt, success or failure — the backoff clock.</summary>
    public DateTime? LastAttemptAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
