using HotChocolate.Execution.Configuration;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// The single place the executable GraphQL schema is assembled (design.md §8).
/// Both <c>Program.cs</c> and the schema-drift test in RocketWiki.Api.Tests call
/// this, so "what the API serves" and "what the drift test builds" can never
/// silently diverge from each other — only from the checked-in schema.graphql,
/// which is exactly the drift the test exists to catch.
/// </summary>
public static class GraphQLConfiguration
{
    public static IRequestExecutorBuilder AddRocketWikiGraphQL(this IRequestExecutorBuilder builder)
    {
        return builder
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            // Explicit types, not left to implicit inference (see each type's own doc
            // for exactly what leak it exists to prevent).
            .AddType<PageType>()
            .AddType<PageRevisionType>()
            .AddType<CommentType>()
            .AddType<AttachmentType>()
            .AddType<SpaceType>()
            .AddType<AccessRuleType>()
            .AddType<AuditEventType>();
    }
}
