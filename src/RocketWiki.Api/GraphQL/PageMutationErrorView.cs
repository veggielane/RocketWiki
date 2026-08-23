using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Flattened, single-type view over <see cref="PageMutationError"/>'s several record
/// subtypes (design.md §8: "every mutation returns a payload type with typed errors").
/// A real GraphQL union per error kind is the more idiomatic shape and a reasonable
/// follow-up; this is the pragmatic version that still surfaces every distinct error
/// fact losslessly (nothing is summarized away) without the extra union-type wiring.
/// <see cref="Kind"/> is the discriminator clients switch on; only the fields relevant
/// to that kind are non-null.
/// </summary>
public sealed record PageMutationErrorView(
    string Kind,
    string? Message,
    int? ExpectedRevisionNumber,
    int? ActualRevisionNumber,
    string? LatestTitle,
    string? LatestContent,
    Guid? SpaceId,
    string? OriginInstanceId,
    int? BlockedPageCount,
    Guid? NotFoundId)
{
    public static PageMutationErrorView From(PageMutationError error) => error switch
    {
        StaleRevisionError e => new PageMutationErrorView(
            "StaleRevision", null, e.ExpectedRevisionNumber, e.ActualRevisionNumber, e.LatestTitle, e.LatestContent, null, null, null, null),

        // OriginInstanceId is what the web's ReadOnlyReplicaDialog renders ("mirrored
        // from <origin> — read-only", design.md §12's banner language).
        ReadOnlyReplicaError e => new PageMutationErrorView(
            "ReadOnlyReplica", null, null, null, null, null, e.SpaceId, e.OriginInstanceId, null, null),

        ForbiddenError e => new PageMutationErrorView(
            "Forbidden", e.Reason, null, null, null, null, null, null, null, null),

        // BlockedPageCount only - never which pages (design.md §6.4.1: their titles may
        // themselves be restricted, so naming them would leak exactly what the
        // restriction protects).
        SubtreeOperationForbiddenError e => new PageMutationErrorView(
            "SubtreeOperationForbidden", null, null, null, null, null, null, null, e.BlockedPageCount, null),

        NotFoundError e => new PageMutationErrorView(
            "NotFound", null, null, null, null, null, null, null, null, e.Id),

        ValidationError e => new PageMutationErrorView(
            "Validation", e.Message, null, null, null, null, null, null, null, null),

        NameTakenError e => new PageMutationErrorView(
            "NameTaken", $"An emoji named '{e.Name}' already exists.", null, null, null, null, null, null, null, null),

        _ => throw new NotSupportedException(
            $"No PageMutationErrorView mapping for '{error.GetType().Name}' - add one before shipping a new PageMutationError subtype."),
    };
}
