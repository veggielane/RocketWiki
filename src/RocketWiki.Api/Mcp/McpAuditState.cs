using RocketWiki.Core.Enums;

namespace RocketWiki.Api.Mcp;

/// <summary>
/// Per-request scratch space a tool uses to describe the audit subject of its call,
/// read back by <see cref="McpServerConfiguration"/>'s call-tool filter when it writes
/// the audit row (design.md §7). The MCP analog of what GraphQL's
/// AuditFieldMiddleware.DescribeSubject infers from the resolved field: the filter
/// cannot inspect an arbitrary tool result, so the tool states its subject explicitly.
///
/// Scoped per HTTP request; in the /mcp endpoint's stateless mode every tool call IS
/// its own HTTP request, so tool and filter resolve the same instance from the same
/// request scope (the SDK hands tools <c>HttpContext.RequestServices</c> directly).
///
/// Fail-closed direction: a tool that never touches this still gets audited by the
/// filter — just with no subject — the same way Query.Spaces audits subject-less.
/// Forgetting this class means a less precise row, never a missing one.
/// </summary>
public sealed class McpAuditState
{
    public AuditSubjectType? SubjectType { get; private set; }
    public Guid? SubjectId { get; private set; }
    public string? SpaceKey { get; private set; }
    public string? DetailsJson { get; private set; }

    public void SetSubject(AuditSubjectType subjectType, Guid subjectId, string? spaceKey = null)
    {
        SubjectType = subjectType;
        SubjectId = subjectId;
        SpaceKey = spaceKey;
    }

    /// <summary>design.md §7: per-action payload (e.g. search query text). Audit is the
    /// sanctioned record for this — never telemetry (§15).</summary>
    public void SetDetails(string detailsJson) => DetailsJson = detailsJson;
}
