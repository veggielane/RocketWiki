using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Telemetry;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using RocketWiki.Data.Services;

namespace RocketWiki.Api.Mcp;

/// <summary>
/// design.md §8/§16 milestone 8: the MCP server at <c>/mcp</c>, hosted <b>in this
/// process</b> deliberately. A separate MCP host would sit outside the API's
/// middleware pipeline — no JIT user provisioning (§11.3), a second DI graph whose
/// service registrations could drift from the real ones, and a second audit wiring to
/// keep honest. In-process, a tool call IS an API request: same JWT validation, same
/// provisioning middleware, same scoped <c>RocketWikiDbContext</c>, same
/// <c>IAuditSink</c>, and the exact service instances GraphQL resolvers use.
///
/// Transport is streamable HTTP in <b>stateless</b> mode: every JSON-RPC message is an
/// independent, individually-authenticated HTTP POST. That is what makes "each call
/// acts as the token's user" literal — there is no session object that could outlive a
/// token, be resumed by a different user, or pin state to one replica. The trade-off,
/// stated honestly: clients on pre-2026-07-28 protocol revisions only send their
/// clientInfo during <c>initialize</c>, which stateless mode doesn't retain, so the
/// audit row's McpClient field is populated only for clients whose protocol carries
/// clientInfo per request (the 2026-07-28 revision) — and is honestly null otherwise.
///
/// <para><b>The <c>Mcp</c> feature flag</b> (docs/CONFIGURATION.md "Feature flags"): with
/// it off, Program.cs calls neither <see cref="AddRocketWikiMcp"/> nor
/// <see cref="MapRocketWikiMcp"/>. "Off" therefore means <i>not mapped</i> — <c>/mcp</c>
/// answers 404 and no McpAuth scheme exists, so the RFC 9728 protected-resource metadata
/// is not served either and nothing advertises a server that is not there. This is the
/// HealthEndpoints precedent (an unmapped endpoint, not a refusing one) rather than a
/// filter inside the pipeline, because a tool pipeline that exists but refuses is still
/// a pipeline whose refusal must be audited and reasoned about; an absent one is not.
/// The tool classes still compile and AuditCoverageTests still sweep them — the
/// declaration rule is about code, not about what is mapped.</para>
/// </summary>
public static class McpServerConfiguration
{
    public static WebApplicationBuilder AddRocketWikiMcp(this WebApplicationBuilder builder)
    {
        // Same ISearchService implementation the GraphQL search field uses (design.md
        // §9.1: FTS on SQL Server, LIKE on SQLite, permission-filtered either way).
        // TryAdd so this composes with the GraphQL-side registration regardless of
        // which lands first — one implementation, never two.
        builder.Services.TryAddScoped<ISearchService, SearchService>();
        builder.Services.AddScoped<McpAuditState>();

        // --- OAuth discovery (MCP spec authorization for HTTP transports) ---
        // Registers the SDK's "McpAuth" scheme WITHOUT touching the app's default
        // schemes. It does two things:
        //   1. Serves RFC 9728 protected-resource metadata at
        //      /.well-known/oauth-protected-resource[/mcp], pointing clients at
        //      Keycloak as the authorization server (design.md §11: Keycloak is
        //      already the OAuth 2.1 AS; no second auth path exists).
        //   2. On a 401 from /mcp, emits WWW-Authenticate: Bearer
        //      resource_metadata="..." so a spec-following client can discover where
        //      to obtain a token.
        // Token VALIDATION stays with the app's default scheme (real Keycloak JWT
        // bearer in production, the fake handler in the SQLite test tier): the
        // post-configure below forwards the McpAuth scheme's authenticate to whatever
        // the default is, while leaving its challenge local so 401s keep the
        // discovery header. No anonymous tool call ever executes either way.
        builder.Services.AddAuthentication().AddMcp();

        // Bound lazily (an options Configure with IOptions<KeycloakOptions> and
        // IConfiguration dependencies) rather than read off builder.Configuration
        // up-front, so configuration layered in after Program's top-level code runs —
        // the WebApplicationFactory .WithWebHostBuilder test path — still lands in the
        // advertised metadata. The authority is KeycloakOptions.ResolveAuthority — the
        // same call the JWT bearer handler makes in Program.cs, so tokens can never
        // validate against one realm while discovery advertises another.
        builder.Services.AddOptions<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<KeycloakOptions>, IConfiguration>((mcpOptions, keycloak, configuration) =>
            {
                mcpOptions.ResourceMetadata = new ProtectedResourceMetadata
                {
                    ResourceName = "RocketWiki",
                };

                if (keycloak.Value.ResolveAuthority(configuration.GetConnectionString("keycloak")) is { } authority)
                {
                    mcpOptions.ResourceMetadata.AuthorizationServers.Add(authority);
                }
            })
            .PostConfigure<IOptions<AuthenticationOptions>>((mcpOptions, authenticationOptions) =>
                mcpOptions.ForwardAuthenticate =
                    authenticationOptions.Value.DefaultAuthenticateScheme
                    ?? authenticationOptions.Value.DefaultScheme);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "RocketWiki", Version = "1.0" };
                options.ServerInstructions =
                    "RocketWiki is a permission-controlled wiki. All tools run as the authenticated " +
                    "user: pages and spaces that user cannot view do not appear in any result and " +
                    "read as nonexistent. Content is Markdown.";
            })
            .WithHttpTransport(options =>
            {
                // Stateless is the SDK default; pinned explicitly because it is
                // load-bearing here (see the class doc): per-call identity, no
                // cross-replica session affinity, no session for anyone to hijack.
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithTools<WikiMcpTools>();

        builder.Services.Configure<McpServerOptions>(AddAuditAndTelemetryFilter);

        return builder;
    }

    public static WebApplication MapRocketWikiMcp(this WebApplication app)
    {
        // RequireAuthenticatedUser is the pipeline-level gate: an anonymous request is
        // 401'd by the authorization middleware before JIT provisioning, the MCP
        // handler, or any tool code runs. The policy authenticates via the McpAuth
        // scheme — which forwards to the app's default (see AddRocketWikiMcp) — so the
        // accompanying challenge carries the resource_metadata discovery header.
        var policy = new AuthorizationPolicyBuilder(McpAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();

        app.MapMcp("/mcp").RequireAuthorization(policy);
        return app;
    }

    /// <summary>
    /// The MCP analog of GraphQL's AuditFieldMiddleware, as a call-tool filter wrapping
    /// every tools/call. In order:
    /// <list type="number">
    /// <item>Stashes the MCP client's self-reported name for the audit row (§7's
    /// McpClient field) — before the tool runs, so any row written during the call
    /// carries it.</item>
    /// <item>Defense-in-depth authentication check (the endpoint policy is the real
    /// gate; an unauthenticated principal reaching here is a bug, and still runs no
    /// tool).</item>
    /// <item>Fail-closed declaration check: a registered tool absent from
    /// <see cref="McpToolAuditRegistry"/> (no [AuditAction]/[NoAudit]) is refused,
    /// not run — the runtime backstop behind AuditCoverageTests' build-time guard.</item>
    /// <item>After a non-error result, writes the declared audit event through
    /// <see cref="IAuditSink"/> — same sink, same fail-closed semantics (§7: if the
    /// audit insert fails, the request fails) as every GraphQL read. The subject
    /// comes from <see cref="McpAuditState"/>, set by the tool.</item>
    /// <item>Records §15 telemetry: one span + counter/duration per call, tagged with
    /// tool name and outcome only — both bounded vocabularies, never arguments,
    /// never content (ApiTelemetry's doc has the details).</item>
    /// </list>
    /// An error result (including the deliberate constant "not found" for
    /// absent-or-restricted subjects, §6.7) is not audited as a success and carries no
    /// denial reason — the same documented repo-wide gap as GraphQL reads, closing
    /// when Core's not-found-vs-denied read result lands.
    /// </summary>
    /// <summary>
    /// Whether a tool with no audit declaration must be refused rather than run.
    ///
    /// <para><b>Refusal is the default, and only positive proof that no such tool exists
    /// lets a call through.</b> This filter is the only runtime enforcement of §7's "no
    /// audit declaration, no tool run", and it used to make refusal conditional on the
    /// tool collection being non-null: a null collection fell through to the SDK, so a
    /// registered-but-undeclared tool would have executed unaudited. A null collection
    /// proves nothing, least of all that the tool is unknown. Fail-closed means an
    /// unanswerable question is answered "no" — the same rule §6.7 applies to a missing
    /// attribute or an unknown group, applied here to audit itself.</para>
    ///
    /// <para>internal + InternalsVisibleTo so the decision is testable on its own, the
    /// same reason <see cref="Audit.CurrentAuditContextAccessor"/> does it: reaching this
    /// branch through the real SDK pipeline needs a registered-but-undeclared tool, which
    /// a green build (AuditCoverageTests) makes impossible to have.</para>
    /// </summary>
    /// <param name="toolIsRegistered">null when the tool collection was unavailable —
    /// "cannot tell", which is not the same answer as "no such tool".</param>
    internal static bool ShouldRefuseUndeclaredTool(string? toolName, bool? toolIsRegistered) =>
        toolName is not null && toolIsRegistered != false;

    private static void AddAuditAndTelemetryFilter(McpServerOptions options)
    {
        options.Filters.Request.CallToolFilters.Add(next =>
        {
            // Captured lazily the same way the SDK's own AuthorizationFilterSetup does:
            // by the time a call flows through, WithTools has populated the collection.
            var toolCollection = options.ToolCollection;

            return async (context, cancellationToken) =>
            {
                // In stateless mode this IS HttpContext.RequestServices — the same
                // scope JIT provisioning and DbAuditSink live in for this request.
                var services = context.Services
                    ?? throw new InvalidOperationException(
                        "MCP tool call has no request services; cannot enforce audit (design.md §7).");

                var httpContextAccessor = services.GetRequiredService<IHttpContextAccessor>();
                if (httpContextAccessor.HttpContext is { } httpContext)
                {
                    httpContext.Items[CurrentAuditContextAccessor.McpClientItemKey] =
                        context.Server.ClientInfo?.Name;
                }

                var toolName = context.Params?.Name;
                string? declaredAction = null;
                var isDeclared = toolName is not null
                    && McpToolAuditRegistry.DeclaredActionsByToolName.TryGetValue(toolName, out declaredAction);

                var toolTag = isDeclared ? toolName! : ApiTelemetry.McpToolUnknown;
                var outcome = ApiTelemetry.McpOutcomeError;
                var startTimestamp = Stopwatch.GetTimestamp();
                using var activity = ApiTelemetry.ActivitySource.StartActivity(ApiTelemetry.McpToolCallSpan);
                activity?.SetTag(ApiTelemetry.McpToolTag, toolTag);

                try
                {
                    if (context.User?.Identity?.IsAuthenticated != true)
                    {
                        outcome = ApiTelemetry.McpOutcomeUnauthenticated;
                        throw new McpException(WikiMcpTools.AuthenticationRequiredMessage);
                    }

                    if (!isDeclared)
                    {
                        // Null collection = "cannot tell", which is NOT "no such tool".
                        bool? toolIsRegistered = toolCollection is null || toolName is null
                            ? null
                            : toolCollection.TryGetPrimitive(toolName, out _);

                        if (!ShouldRefuseUndeclaredTool(toolName, toolIsRegistered))
                        {
                            // Nothing to audit and nothing to run: let the SDK produce its
                            // standard unknown-tool error.
                            outcome = ApiTelemetry.McpOutcomeUnknownTool;
                            return await next(context, cancellationToken);
                        }

                        // Either the tool is registered but undeclared, or we cannot tell.
                        // A green build makes the first unreachable (AuditCoverageTests);
                        // both refuse.
                        outcome = ApiTelemetry.McpOutcomeUndeclared;
                        throw new InvalidOperationException(
                            $"MCP tool '{toolName}' has no audit declaration " +
                            "([AuditAction]/[NoAudit], design.md §7) and will not be executed.");
                    }

                    var result = await next(context, cancellationToken);

                    if (result.IsError is true)
                    {
                        outcome = ApiTelemetry.McpOutcomeError;
                        return result;
                    }

                    if (declaredAction is { } action)
                    {
                        var auditState = services.GetRequiredService<McpAuditState>();
                        var sink = services.GetRequiredService<IAuditSink>();

                        // §7: synchronous, fail-closed — if this insert throws, the
                        // tool call fails even though the tool itself succeeded.
                        await sink.RecordAsync(
                            new AuditRecord(
                                action,
                                AuditOutcome.Success,
                                auditState.SubjectType,
                                auditState.SubjectId,
                                auditState.SpaceKey,
                                auditState.DetailsJson),
                            cancellationToken);
                    }

                    outcome = ApiTelemetry.McpOutcomeSuccess;
                    return result;
                }
                finally
                {
                    activity?.SetTag(ApiTelemetry.McpOutcomeTag, outcome);
                    ApiTelemetry.RecordMcpToolCall(toolTag, outcome, Stopwatch.GetElapsedTime(startTimestamp));
                }
            };
        });
    }
}
