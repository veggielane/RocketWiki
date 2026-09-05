using System.Reflection;
using RocketWiki.Api.RealTime;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The hub's client-invokable surface, pinned. SignalR dispatches every public instance
/// method a hub declares, so this list IS the wire contract the SPA's transports
/// (web/src/realtime/types.ts) are written against: presence joins and leaves, and the
/// co-editing relay.
///
/// <para>Live mouse pointers (<c>PointerMove</c> in, <c>PointerMoved</c> out) were removed
/// deliberately — presence is "who else is here", not where their mouse is. An absence is
/// the one thing no other test can see, so the exact-list assertion below is what turns
/// the removal into something that fails if the method quietly comes back. Adding a hub
/// method is a contract change and should have to touch this list.</para>
/// </summary>
public class NotificationsHubSurfaceTests
{
    /// <summary>
    /// The same filter AuditCoverageTests sweeps with: public instance methods this hub
    /// declares, minus overrides of the Hub base lifecycle (OnConnectedAsync/
    /// OnDisconnectedAsync), which SignalR never dispatches to clients.
    /// </summary>
    private static List<string> ClientInvokableMethodNames() => typeof(NotificationsHub)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => !m.IsSpecialName)
        .Where(m => m.GetBaseDefinition().DeclaringType == typeof(NotificationsHub))
        .Select(m => m.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void TheClientInvokableSurfaceIsExactlyTheDocumentedContract()
    {
        Assert.Equal(
            [
                "JoinEditSession",
                "JoinPage",
                "JoinRoom",
                "LeaveEditSession",
                "LeavePage",
                "LeaveRoom",
                "PushAwareness",
                "PushUpdate",
                "ReseedEditSession",
            ],
            ClientInvokableMethodNames());
    }

    [Fact]
    public void NothingOnTheHubMovesAPointer()
    {
        // Belt to the exact list above, and it reads as what it is: a pointer method
        // under any name, declared here or inherited, is the feature coming back.
        var everything = typeof(NotificationsHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        Assert.NotEmpty(everything);
        Assert.DoesNotContain(everything, name => name.Contains("Pointer", StringComparison.OrdinalIgnoreCase));
    }
}
