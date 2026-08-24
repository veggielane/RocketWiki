using System.Reflection;
using HotChocolate.Types;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.Server;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.RealTime;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// Enforces design.md §7/§8: "every root field... declares its audit action;
/// a schema test fails the build on undeclared fields" — extended to every
/// channel §7 names. Four sweeps, one per surface a user action can enter
/// through: GraphQL root fields, MCP tools, hub methods, and the minimal-API
/// binary routes. Whichever surface grows an entry point, it cannot ship
/// without stating its audit decision.
///
/// This only checks the declaration (design.md §7's "which action, if any")
/// exists and is unambiguous - not that dispatch is wired for it. Root query
/// fields get dispatch automatically via [UseAuditDispatch] +
/// AuditFieldMiddleware; mutations audit success through the domain-event
/// pipeline instead and denial explicitly in the resolver (see Mutation.cs);
/// routes and the hub emit their rows by hand - this test can't see any of
/// that, only that a declaration exists.
/// </summary>
public class AuditCoverageTests
{
    private static readonly Type[] RootTypes = [typeof(Query), typeof(Mutation)];

    /// <summary>
    /// Sweeps everything Hot Chocolate would infer as a root field: public
    /// instance AND static methods (static resolvers are inferred too), public
    /// properties (a property-declared field would otherwise hide behind the
    /// IsSpecialName filter that skips its accessors), and — should one ever
    /// appear — types extending a root via [ExtendObjectType(typeof(Query))] /
    /// [ExtendObjectType(typeof(Mutation))]. Type extensions of non-root types
    /// (PageFieldResolvers, SpaceFieldResolvers, PageTreeNodeTypeExtension)
    /// stay deliberately out of scope: their fields are nested resolutions
    /// under an already-audited root read, not user actions of their own
    /// (design.md §8's nested-field rule).
    /// </summary>
    [Fact]
    public void EveryRootField_DeclaresExactlyOneOfAuditActionOrNoAudit()
    {
        var problems = new List<string>();
        var fieldMembers = new List<(Type DeclaringType, MemberInfo Member)>();

        var apiAssemblyTypes = typeof(Query).Assembly.GetTypes();

        foreach (var rootType in RootTypes)
        {
            var contributingTypes = new List<Type> { rootType };
            contributingTypes.AddRange(apiAssemblyTypes.Where(t =>
                t.GetCustomAttribute<ExtendObjectTypeAttribute>() is { } extension
                && (extension.ExtendsType == rootType || extension.Name == rootType.Name)));

            foreach (var type in contributingTypes)
            {
                const BindingFlags fields =
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                fieldMembers.AddRange(type.GetMethods(fields)
                    .Where(m => !m.IsSpecialName) // accessor methods; the property itself is swept below
                    .Select(m => (type, (MemberInfo)m)));
                fieldMembers.AddRange(type.GetProperties(fields)
                    .Select(p => (type, (MemberInfo)p)));
            }
        }

        // Non-vacuous: the roots have real fields; an empty sweep means the
        // reflection filters broke, not that the schema went quiet.
        Assert.NotEmpty(fieldMembers);

        foreach (var (declaringType, member) in fieldMembers)
        {
            var hasAction = member.GetCustomAttribute<AuditActionAttribute>() is not null;
            var hasNoAudit = member.GetCustomAttribute<NoAuditAttribute>() is not null;

            switch (hasAction, hasNoAudit)
            {
                case (false, false):
                    problems.Add($"{declaringType.Name}.{member.Name}: no [AuditAction] or [NoAudit] declared.");
                    break;
                case (true, true):
                    problems.Add($"{declaringType.Name}.{member.Name}: both [AuditAction] and [NoAudit] declared — pick one.");
                    break;
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

    /// <summary>
    /// The declaration rule, extended to the realtime channel (design.md §7: every
    /// channel lands in the same audit table; §8 co-editing made the hub carry an
    /// audited, canEdit-consuming action for the first time). Sweeps every
    /// client-invokable method on the hub — public instance methods this hub declares,
    /// excluding overrides of the Hub base lifecycle (OnConnectedAsync/
    /// OnDisconnectedAsync, which SignalR never dispatches to clients). Presence
    /// methods carry [NoAudit] with §8's stated reasoning; edit-session join/leave
    /// carry [AuditAction], emitted by the hub itself on AuditChannel.Realtime (the
    /// attribute is the declaration, emission is manual — the same split mutations use).
    /// </summary>
    [Fact]
    public void EveryHubMethodDeclaresExactlyOneOfAuditActionOrNoAudit()
    {
        var problems = new List<string>();

        var hubMethods = typeof(NotificationsHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            // Overrides of Hub's own members (lifecycle callbacks, Dispose) are not
            // client-invokable: SignalR's dispatcher excludes methods whose base
            // definition lives on the Hub base type.
            .Where(m => m.GetBaseDefinition().DeclaringType == typeof(NotificationsHub))
            .ToList();

        // Non-vacuous: the hub has real methods; an empty sweep means the reflection
        // filter broke, not that the hub went quiet.
        Assert.NotEmpty(hubMethods);

        foreach (var method in hubMethods)
        {
            var hasAction = method.GetCustomAttribute<AuditActionAttribute>() is not null;
            var hasNoAudit = method.GetCustomAttribute<NoAuditAttribute>() is not null;

            switch (hasAction, hasNoAudit)
            {
                case (false, false):
                    problems.Add($"NotificationsHub.{method.Name}: no [AuditAction] or [NoAudit] declared.");
                    break;
                case (true, true):
                    problems.Add($"NotificationsHub.{method.Name}: both [AuditAction] and [NoAudit] declared — pick one.");
                    break;
            }
        }

        Assert.True(problems.Count == 0,
            "Every client-invokable hub method must declare exactly one of [AuditAction] or [NoAudit] " +
            "(design.md §7/§8). Problems found:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// The declaration rule, extended to the minimal-API binary routes (design.md
    /// §7: the `attachment` channel — /attachments, /avatars, /emojis — lands in
    /// the same audit table as every other channel). Handler discovery relies on
    /// the house convention this test also enforces: route handlers are named
    /// static methods returning Task&lt;IResult&gt; (or Task of a typed IResult) on
    /// the static *Endpoints classes — a handler written inline as a lambda can't
    /// be swept for a declaration, so a class whose handlers vanish into lambdas
    /// fails the per-class non-vacuity check below rather than passing silently.
    /// As everywhere else, the attribute is the declaration only: emission stays
    /// where it is (domain-event pipeline for mutation successes, explicit denial
    /// rows in the handlers, deliberate silence on the display-asset GETs).
    /// </summary>
    [Fact]
    public void EveryEndpointRouteHandlerDeclaresExactlyOneOfAuditActionOrNoAudit()
    {
        var problems = new List<string>();
        var handlerCount = 0;

        // Discovery by convention, not a hard-coded list: a fourth *Endpoints
        // class is swept the day it appears. IsAbstract && IsSealed is C# for
        // `static class`.
        var endpointClasses = typeof(Api.Attachments.AttachmentEndpoints).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: true, IsSealed: true }
                && t.Name.EndsWith("Endpoints", StringComparison.Ordinal))
            .ToList();

        // Non-vacuous: the three known endpoint classes exist; an empty set means
        // discovery broke (renamed, moved), and this guard must fail rather than
        // silently pass over nothing.
        Assert.NotEmpty(endpointClasses);

        foreach (var endpointClass in endpointClasses)
        {
            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var handlers = endpointClass.GetMethods(all).Where(IsRouteHandlerShaped).ToList();

            if (handlers.Count == 0)
            {
                problems.Add(
                    $"{endpointClass.Name}: no static Task<IResult> handler methods found — if its routes " +
                    "became lambdas or changed shape, restore named handler methods so their audit " +
                    "declarations stay sweepable.");
                continue;
            }

            foreach (var handler in handlers)
            {
                handlerCount++;

                var hasAction = handler.GetCustomAttribute<AuditActionAttribute>() is not null;
                var hasNoAudit = handler.GetCustomAttribute<NoAuditAttribute>() is not null;

                switch (hasAction, hasNoAudit)
                {
                    case (false, false):
                        problems.Add($"{endpointClass.Name}.{handler.Name}: no [AuditAction] or [NoAudit] declared.");
                        break;
                    case (true, true):
                        problems.Add($"{endpointClass.Name}.{handler.Name}: both [AuditAction] and [NoAudit] declared — pick one.");
                        break;
                }
            }
        }

        Assert.True(handlerCount > 0, "No route handler methods found on any *Endpoints class.");
        Assert.True(problems.Count == 0,
            "Every minimal-API route handler must declare exactly one of [AuditAction] or [NoAudit] " +
            "(design.md §7/§8). Problems found:\n" + string.Join('\n', problems));
    }

    /// <summary>Route handlers are async and produce a result; helpers on the same
    /// classes are sync IResult factories (PayloadTooLarge, ErrorResult, PngResult)
    /// or plain Task utilities — this shape check separates the two without a
    /// hard-coded name list. Task of a typed result (Results&lt;Ok, NotFound&gt;)
    /// counts too: it implements IResult.</summary>
    private static bool IsRouteHandlerShaped(MethodInfo method) =>
        method.ReturnType is { IsGenericType: true } returnType
        && returnType.GetGenericTypeDefinition() == typeof(Task<>)
        && typeof(Microsoft.AspNetCore.Http.IResult).IsAssignableFrom(returnType.GetGenericArguments()[0]);
}
