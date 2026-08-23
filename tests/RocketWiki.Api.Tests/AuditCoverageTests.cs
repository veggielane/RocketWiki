using System.Reflection;
using ModelContextProtocol.Server;
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

    /// <summary>
    /// The same declaration rule, extended to the MCP channel (design.md §7: "the
    /// attachment routes and MCP tools carry the same declarations through their own
    /// pipelines"). Sweeps every [McpServerToolType] class in the Api assembly — the
    /// identical universe McpToolAuditRegistry builds its runtime map from, so this
    /// build-time guard and the call-tool filter's fail-closed check can never cover
    /// different sets of tools. Also requires every tool's Name to be set explicitly
    /// on the attribute: the registry keys on that name, the SDK registers it, and
    /// design.md §8 publishes it — deriving it from the C# method name would let the
    /// three drift.
    /// </summary>
    [Fact]
    public void EveryMcpToolDeclaresExactlyOneOfAuditActionOrNoAudit_AndAnExplicitName()
    {
        var problems = new List<string>();
        var toolMethodCount = 0;

        var toolTypes = typeof(Api.Mcp.WikiMcpTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();

        // Non-vacuous: if the tool classes vanish (renamed, moved), this guard must
        // fail rather than silently pass over an empty set.
        Assert.NotEmpty(toolTypes);

        foreach (var toolType in toolTypes)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in toolType.GetMethods(all))
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool is null)
                {
                    continue;
                }

                toolMethodCount++;

                if (string.IsNullOrWhiteSpace(tool.Name))
                {
                    problems.Add($"{toolType.Name}.{method.Name}: [McpServerTool] must set Name explicitly.");
                }

                var hasAction = method.GetCustomAttribute<AuditActionAttribute>() is not null;
                var hasNoAudit = method.GetCustomAttribute<NoAuditAttribute>() is not null;

                switch (hasAction, hasNoAudit)
                {
                    case (false, false):
                        problems.Add($"{toolType.Name}.{method.Name}: no [AuditAction] or [NoAudit] declared.");
                        break;
                    case (true, true):
                        problems.Add($"{toolType.Name}.{method.Name}: both [AuditAction] and [NoAudit] declared — pick one.");
                        break;
                }
            }
        }

        Assert.True(toolMethodCount > 0, "No [McpServerTool] methods found on any [McpServerToolType] class.");
        Assert.True(problems.Count == 0,
            "Every MCP tool must declare exactly one of [AuditAction] or [NoAudit], and an explicit " +
            "tool Name (design.md §7/§8). Problems found:\n" + string.Join('\n', problems));
    }
}
