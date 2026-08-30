using RocketWiki.Api.Mcp;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §7: every tool declares its audit action, and "no audit declaration, no tool
/// run" is enforced at runtime by the MCP call filter — the only place that invariant is
/// enforced rather than merely asserted at build time.
///
/// <para>The branch under test had refusal conditional on the SDK's tool collection being
/// available, so "I cannot tell whether this tool exists" fell through to running it
/// unaudited. Reachability was low, which is exactly why it needs a test rather than an
/// argument: the guard is unreachable through the real pipeline while the build is green
/// (AuditCoverageTests makes a registered-but-undeclared tool impossible to have), so
/// nothing else can ever exercise it.</para>
/// </summary>
public class McpUndeclaredToolGateTests
{
    [Fact]
    public void A_registered_but_undeclared_tool_is_refused()
    {
        Assert.True(McpServerConfiguration.ShouldRefuseUndeclaredTool("searchPages", toolIsRegistered: true));
    }

    [Fact]
    public void A_tool_that_provably_does_not_exist_is_passed_to_the_sdk()
    {
        // Nothing to audit and nothing to run — the SDK's own unknown-tool error is the
        // right answer, and refusing here would replace it with a confusing one.
        Assert.False(McpServerConfiguration.ShouldRefuseUndeclaredTool("noSuchTool", toolIsRegistered: false));
    }

    [Fact]
    public void A_tool_whose_existence_cannot_be_determined_is_refused()
    {
        // The whole finding. A null tool collection is "cannot tell", and the old code
        // read it as "no such tool" and ran the call. An unanswerable question is
        // answered "no" — §6.7's rule for a missing attribute, applied to audit itself.
        Assert.True(McpServerConfiguration.ShouldRefuseUndeclaredTool("searchPages", toolIsRegistered: null));
    }

    [Fact]
    public void A_call_naming_no_tool_at_all_is_passed_to_the_sdk()
    {
        // There is no tool here to run unaudited, so there is nothing to fail closed
        // about; the SDK rejects a call with no name on its own terms.
        Assert.False(McpServerConfiguration.ShouldRefuseUndeclaredTool(null, toolIsRegistered: null));
    }
}
