namespace RocketWiki.Api.Identity;

/// <summary>
/// The configured local instance id (design.md §12: every instance has an
/// <c>InstanceId</c>; <c>Space.OriginInstanceId</c> equal to it means native, anything
/// else means replica). Program.cs already resolves this once from <c>Instance:Id</c>
/// and closes over it when constructing the mutation services; this singleton makes the
/// same value reachable from GraphQL resolvers (Query.SyncStatus) without re-reading
/// configuration in resolver code.
/// </summary>
public sealed record InstanceIdentity(string LocalInstanceId);
