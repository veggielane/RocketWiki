using System.Reflection;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// Enforces design.md §7/§8: "every root field... declares its audit action;
/// a schema test fails the build on undeclared fields." With only the
/// placeholder `me` field this is close to a no-op today — that's the point.
/// The seam has to exist before there's anything to forget to declare, or it
/// never gets built at all once the schema has fifty fields instead of one.
///
/// This only checks the declaration (design.md §7's "which action, if any")
/// exists and is unambiguous - not that dispatch is wired for it. Root query
/// fields get dispatch automatically via [UseAuditDispatch] +
/// AuditFieldMiddleware; mutations audit success through the domain-event
/// pipeline instead and denial explicitly in the resolver (see Mutation.cs) -
/// this test can't see either of those, only that a declaration exists.
/// </summary>
public class AuditCoverageTests
{
    private static readonly Type[] RootTypes = [typeof(Query), typeof(Mutation)];

    [Fact]
    public void EveryRootField_DeclaresExactlyOneOfAuditActionOrNoAudit()
    {
        var problems = new List<string>();

        foreach (var rootType in RootTypes)
        {
            var fields = rootType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName); // skip property accessors etc.

            foreach (var field in fields)
            {
                var hasAction = field.GetCustomAttribute<AuditActionAttribute>() is not null;
                var hasNoAudit = field.GetCustomAttribute<NoAuditAttribute>() is not null;

                switch (hasAction, hasNoAudit)
                {
                    case (false, false):
                        problems.Add($"{rootType.Name}.{field.Name}: no [AuditAction] or [NoAudit] declared.");
                        break;
                    case (true, true):
                        problems.Add($"{rootType.Name}.{field.Name}: both [AuditAction] and [NoAudit] declared — pick one.");
                        break;
                }
            }
        }

        Assert.True(problems.Count == 0,
            "Every root Query/Mutation field must declare exactly one of [AuditAction] or [NoAudit] " +
            "(design.md §7/§8). Problems found:\n" + string.Join('\n', problems));
    }
}
