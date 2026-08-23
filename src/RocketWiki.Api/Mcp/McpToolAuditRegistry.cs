using System.Reflection;
using ModelContextProtocol.Server;
using RocketWiki.Api.Audit;

namespace RocketWiki.Api.Mcp;

/// <summary>
/// Maps every MCP tool name to its declared audit action (design.md §7: "every root
/// field and route declares its audit action" — for MCP, the tool is the root). Built
/// once by reflection over every <see cref="McpServerToolTypeAttribute"/> class in this
/// assembly, i.e. the same universe AuditCoverageTests sweeps at build time, so the
/// build-time guard and this runtime map cannot cover different sets of tools.
///
/// A tool with <see cref="AuditActionAttribute"/> maps to its action; a tool with
/// <see cref="NoAuditAttribute"/> maps to null (declared, deliberately unaudited — none
/// exist today). A registered tool with NEITHER never reaches here in a green build
/// (AuditCoverageTests fails first), but if one does, the call-tool filter refuses to
/// execute it — fail closed, §7 style: no audit declaration, no tool run.
/// </summary>
internal static class McpToolAuditRegistry
{
    /// <summary>Value is the audit action, or null for a declared [NoAudit] tool.
    /// A tool name absent from this dictionary entirely is undeclared.</summary>
    public static IReadOnlyDictionary<string, string?> DeclaredActionsByToolName { get; } = Build();

    private static Dictionary<string, string?> Build()
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var type in typeof(McpToolAuditRegistry).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in type.GetMethods(all))
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool is null)
                {
                    continue;
                }

                // Explicit names are enforced by AuditCoverageTests; the method-name
                // fallback exists only so an undeclared probe still lands in the map
                // deterministically rather than silently vanishing.
                var toolName = tool.Name ?? method.Name;

                if (method.GetCustomAttribute<AuditActionAttribute>() is { } action)
                {
                    map[toolName] = action.Action;
                }
                else if (method.GetCustomAttribute<NoAuditAttribute>() is not null)
                {
                    map[toolName] = null;
                }

                // Neither attribute: deliberately NOT added — the filter treats an
                // unmapped-but-registered tool as undeclared and refuses to run it.
            }
        }

        return map;
    }
}
