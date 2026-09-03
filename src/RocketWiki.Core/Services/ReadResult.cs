namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §6.7: read services return an <b>internal</b> result distinguishing
/// "not found" from "denied (with reason)", because §7 requires denials to be audited
/// with the failing restriction — which the read service knows and the API layer
/// cannot infer from a bare null. "Internal" means internal to the system, not C#
/// <c>internal</c>: the API layer consumes this type, audits the <see cref="Denied"/>
/// case, and then collapses <see cref="NotFound"/> and <see cref="Denied"/> to the
/// exact same null/empty/404 response shape. The distinction exists throughout the
/// system and dies exactly once, at the response edge — it must never reach a GraphQL
/// type, an HTTP status split, or any other caller-observable surface, or the
/// distinction itself becomes the existence leak §6.7 forbids.
/// </summary>
public abstract record ReadResult<T> where T : class
{
    private ReadResult()
    {
    }

    public sealed record Found(T Value) : ReadResult<T>;

    /// <summary>No such subject exists. No access decision was made — there was nothing to decide about.</summary>
    public sealed record NotFound : ReadResult<T>;

    /// <summary>
    /// The subject exists but the principal failed canView. <paramref name="Reason"/> is
    /// the specific failing-restriction reason the rule engine already computed —
    /// <c>restriction:{pageId}:{ruleId}</c> for a failed page restriction, or
    /// <c>no-space-access</c> (see EffectivePermissionCalculator). Per design.md §15's
    /// convention this specific form belongs in the audit row's details; telemetry only
    /// ever sees the bounded category (CoreTelemetry.CategorizeDenialReason), which the
    /// calculator already emits itself — nothing here feeds a metric tag.
    /// </summary>
    public sealed record Denied(string Reason) : ReadResult<T>;

    /// <summary>
    /// The collapse for callers that need only "viewable or not" (presence checks, the
    /// DataLoader): Found's value, or null for both NotFound and Denied. Callers on a
    /// user-facing read path must NOT use this shortcut without first auditing the
    /// Denied case (design.md §7) — pattern-match instead.
    /// </summary>
    public T? ValueOrNull() => this is Found found ? found.Value : null;
}
