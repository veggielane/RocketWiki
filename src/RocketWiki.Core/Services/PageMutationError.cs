namespace RocketWiki.Core.Services;

/// <summary>
/// design.md §8: "every mutation returns a payload type with typed errors." These are
/// the provider-agnostic error facts; the GraphQL layer maps them onto its own typed
/// error unions (StaleRevisionError, ReadOnlyReplicaError, ...) without RocketWiki.Core
/// or RocketWiki.Data needing any Hot Chocolate dependency.
/// </summary>
public abstract record PageMutationError;

/// <summary>design.md §5/§8: the editor's revision number is behind the current one - a conflict, not a transport error.</summary>
public sealed record StaleRevisionError(int ExpectedRevisionNumber, int ActualRevisionNumber, string LatestTitle, string LatestContent) : PageMutationError;

/// <summary>
/// design.md §6.4/§12: canEdit is unconditionally false on a replica space, beneath
/// every grant. Carries the space's OriginInstanceId so clients can say "mirrored from
/// LOW — read-only" (design.md §12's banner; the web's ReadOnlyReplicaDialog renders
/// exactly this fact) instead of a bare refusal.
/// </summary>
public sealed record ReadOnlyReplicaError(Guid SpaceId, string OriginInstanceId) : PageMutationError;

/// <summary>canView/canEdit denied - Reason mirrors EffectivePermissionCalculator's denial reason for the permission inspector (design.md §6.6).</summary>
public sealed record ForbiddenError(string Reason) : PageMutationError;

/// <summary>
/// design.md §6.4.1: a structural operation (move/delete/restore) touches more than one
/// page, and at least one of them failed canEdit. Reports only a count, never which
/// pages - their titles may themselves be restricted, so naming them would leak exactly
/// the information the restriction exists to protect.
/// </summary>
public sealed record SubtreeOperationForbiddenError(int BlockedPageCount) : PageMutationError;

public sealed record NotFoundError(Guid Id) : PageMutationError;

/// <summary>
/// A uniqueness conflict on a caller-chosen key (custom emoji names). Distinct from
/// <see cref="ValidationError"/> because the input is well-formed — the fix is a
/// different name, not a corrected one — and mapped to HTTP 409 on binary routes.
/// Echoing the name back leaks nothing: the caller supplied it.
/// </summary>
public sealed record NameTakenError(string Name) : PageMutationError;

public sealed record ValidationError(string Message) : PageMutationError;

/// <summary>design.md §12: "if bundle 41 hasn't been applied, 42 waits" - a bundle numbered ahead of the next expected one is refused, never silently absorbed.</summary>
public sealed record BundleGapError(int ExpectedBundleNumber, int ActualBundleNumber) : PageMutationError;

/// <summary>design.md §12: the manifest hash chain caught a reordered or tampered bundle - PreviousManifestHash didn't match the last applied bundle's manifest.</summary>
public sealed record BundleChainMismatchError(string Reason) : PageMutationError;

/// <summary>The events.ndjson bytes inside the bundle don't hash to manifest.PayloadSha256 - corruption or tampering in transit.</summary>
public sealed record BundlePayloadTamperedError(string Reason) : PageMutationError;

/// <summary>data-model.md: SyncSpaceState.AppliedSequence is the finer-grained, per-space gap check that sits inside the coarser per-bundle one.</summary>
public sealed record SpaceSequenceGapError(Guid SpaceId, long ExpectedSequence, long ActualSequence) : PageMutationError;

/// <summary>
/// The bundle's manifest declares a format version newer than this instance understands
/// (RocketWiki.Core.Sync.BundleFormat). Refused before any event is parsed - a newer
/// format is never partially understood, per the same refuse-don't-absorb philosophy as
/// the hash chain. The fix is operational: upgrade this instance, then re-import.
/// </summary>
public sealed record BundleFormatUnsupportedError(int BundleFormatVersion, int MaxSupportedVersion) : PageMutationError;

/// <summary>
/// design.md §12: the bundle claims to originate from THIS instance, so importing it
/// would land its spaces as native rather than replica — <see cref="Entities.Space.IsReplicaOf"/>
/// compares <c>OriginInstanceId</c> against the local id, so an equal pair makes
/// <c>canEdit</c> answer normally and every mirrored space becomes writable. That is a
/// silent fail-OPEN on the invariant one-way sync rests on, and the shape it arrives in is
/// mundane: two instances both left on the default identity.
///
/// <para>Refused before any event is applied, like every other integrity refusal, because
/// "these are my own spaces coming back at me" is not a state to absorb halfway. The fix
/// is operational — give the two instances distinct <c>Instance:Id</c> values.</para>
/// </summary>
public sealed record BundleSelfOriginError(string InstanceId) : PageMutationError;
