using HotChocolate.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RocketWiki.Api.Attachments;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Embeddings;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Mcp;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Database (design.md §14/§15) ---
// Official Aspire client integration: wires RocketWikiDbContext to the
// "rocketwiki" connection string the AppHost injects via service discovery
// (the database resource name in AppHost.cs), plus a health check, retry, and
// OpenTelemetry for free — the idiomatic Aspire path rather than hand-rolled
// AddDbContext/UseSqlServer.
builder.AddSqlServerDbContext<RocketWikiDbContext>("rocketwiki");

// --- File storage (design.md §10) ---
builder.Services.AddFileStorage(builder.Configuration);

// --- Authentication: JWT bearer against Keycloak (design.md §11) ---
// Authority is derived from the Keycloak connection string the AppHost injects
// via service discovery (design.md §15 "config by reference, not by hand"),
// combined with a configurable realm name. Keycloak:Authority is the fallback
// for running the API standalone, outside Aspire.
var keycloakBaseUrl = builder.Configuration.GetConnectionString("keycloak");
var realm = builder.Configuration["Keycloak:Realm"] ?? "rocketwiki";
var authority = builder.Configuration["Keycloak:Authority"]
    ?? (keycloakBaseUrl is not null ? $"{keycloakBaseUrl.TrimEnd('/')}/realms/{realm}" : null);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = builder.Configuration["Keycloak:Audience"] ?? "rocketwiki";
        // The dev Keycloak container serves plain HTTP; production Keycloak sits
        // inside the network's security boundary behind TLS (design.md §9.4/§15).
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        // SignalR (design.md §8): a browser cannot set an Authorization header on a
        // WebSocket upgrade request, so the JS client sends the token via the
        // `access_token` query string instead - the standard, documented workaround
        // for ASP.NET Core SignalR + JWT bearer. Scoped to /hubs only, so a token
        // leaking into a URL (proxy/server logs) is a risk taken deliberately for
        // exactly one path, not silently widened to the whole API.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// --- Request-scoped identity and audit context (design.md §7, §11.3) ---
// Both accessors below are backed by HttpContext (Items / IHttpContextAccessor),
// not their own scoped-service instance state: Hot Chocolate resolves [Service]
// parameters from a different DI scope than app.UseMiddleware<T>() classic
// middleware uses for the same request (confirmed by instrumenting both sites -
// two different instances of the same registered scoped service), so anything
// that must be *set* by middleware and *read* inside a resolver has to go through
// HttpContext itself, the one thing reliably shared across that boundary.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActingUserAccessor, ActingUserAccessor>();
builder.Services.AddScoped<ICurrentAuditContextAccessor, CurrentAuditContextAccessor>();
builder.Services.AddScoped<IAuditSink, DbAuditSink>();

// The ABAC Principal (design.md §6.1) — built fresh from the validated token on every
// request, never from the local User mirror.
builder.Services.AddScoped<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>();

// design.md §6.5: Keycloak's realm-level "admin" role, surfaced via the "roles"
// protocol mapper (rocketwiki-realm.json) — deliberately not part of the ABAC
// Principal above (EffectivePermissionCalculator has no notion of "instance admin").
builder.Services.AddScoped<IInstanceRoleAccessor, InstanceRoleAccessor>();

// --- Page services (design.md §6.7/§8) ---
// PageService needs the local InstanceId to tell native spaces from replicas
// (design.md §12); Instance:Id is a plain config value, not yet wired to anything
// sync-related since low/high sync itself is a later milestone.
var localInstanceId = builder.Configuration["Instance:Id"] ?? "standalone";
// Same value as a DI-visible singleton, for resolvers that need to distinguish native
// from replica or report it (Query.SyncStatus) rather than construct services with it.
builder.Services.AddSingleton(new InstanceIdentity(localInstanceId));
builder.Services.AddScoped<IPageReadService, PageReadService>();
builder.Services.AddScoped<IPageService>(sp => new PageService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<ICommentService>(sp => new CommentService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<ILabelService>(sp => new LabelService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));

// --- Attachments (design.md §10) — metadata + IFileStorage (already registered above) ---
builder.Services.AddScoped<IAttachmentReadService, AttachmentReadService>();
builder.Services.AddScoped<IAttachmentService>(sp =>
    new AttachmentService(sp.GetRequiredService<RocketWikiDbContext>(), sp.GetRequiredService<IFileStorage>(), localInstanceId));

// --- Spaces and access rules (design.md §6.5/§8) ---
builder.Services.AddScoped<ISpaceService>(sp => new SpaceService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<IAccessRuleService, AccessRuleService>();

// --- Search (design.md §9.1/§9.3, milestones 4 + 7) ---
// One implementation, self-detecting provider: SQL Server FTS in production, the
// LIKE fallback everywhere else (which is what the SQLite test tier exercises —
// the FTS path stays TODO-flagged/unverified until the Testcontainers tier exists).
// Hybrid keyword+vector RRF when the embedding pipeline below is configured; the
// generator/options parameters default to null otherwise and search is keyword-only.
builder.Services.AddScoped<ISearchService, SearchService>();

// --- Embedding pipeline (design.md §9.2/§9.3, milestone 7) ---
// IEmbeddingGenerator over the OpenAI-compatible endpoint from the Aspire "embeddings"
// connection string / Ai section, the EmbeddingIndexer, and the polling background job
// that (re-)embeds changed pages. Registers NOTHING when no endpoint is configured —
// search then degrades to keyword-only and saves are never affected (§9.2). See
// EmbeddingPipelineConfiguration for config precedence and §9.4's boundary requirement.
builder.AddRocketWikiEmbeddings();

// --- Watches and the persisted notification list (design.md §8, milestone 4b) ---
builder.Services.AddScoped<IWatchService>(sp => new WatchService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<INotificationReadModelService>(sp =>
    new NotificationReadModelService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));

// --- MCP server (design.md §8, milestone 8) ---
// /mcp in this same process/pipeline: OAuth discovery against Keycloak (derived from
// the same Keycloak:* configuration as the JWT bearer authority above), the same
// bearer identity and JIT provisioning as GraphQL, read-only tools over the shared
// service layer, and per-call audit on AuditChannel.Mcp. See McpServerConfiguration
// for why in-process (and stateless) is load-bearing, not a convenience.
builder.AddRocketWikiMcp();

// --- Real-time: SignalR (design.md §8) ---
// design.md §15: a Redis backplane is needed once replicas > 1; at one replica (today)
// the default in-memory backplane is correct and this is a real decision to revisit at
// deployment time, not a gap in this wiring.
builder.Services
    .AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment())
    .AddMessagePackProtocol();

// Singleton: ephemeral, in-memory connection/presence state that must be shared across
// every request and Hub invocation, not scoped per-request the way RocketWikiDbContext
// is (design.md §8: presence "no table, no audit rows" — this IS the only copy).
builder.Services.AddSingleton<IRealtimeConnectionRegistry, RealtimeConnectionRegistry>();
builder.Services.AddSingleton<IPresenceRuleChangeNotifier, PresenceRuleChangeNotifier>();
builder.Services.AddScoped<INotificationDispatcher>(sp =>
    new NotificationDispatcher(
        sp.GetRequiredService<RocketWikiDbContext>(),
        sp.GetRequiredService<IRealtimeConnectionRegistry>(),
        sp.GetRequiredService<IHubContext<NotificationsHub>>(),
        localInstanceId));

// --- GraphQL (Hot Chocolate, design.md §8) ---
// Page, Space, Comment/Label/Attachment reads, auditEvents, and search (milestone 4)
// are wired.
// TODO(milestone 8/MCP): the object-level canView enforcement here (PageFieldResolvers,
// routing every Page-returning field through IPageReadService) is the same enforcement
// MCP tools must reuse once they exist (design.md §6.7/§8) — same service, same rules.
builder.Services
    .AddGraphQLServer()
    .AddRocketWikiGraphQL()
    // Hot Chocolate hides resolver exception details by default regardless of the
    // ASP.NET Core hosting environment - a separate setting, deliberately opt-in only
    // for Development so a production error response never carries a stack trace.
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment())
    // --- GraphQL tracing (design.md §15) ---
    // HotChocolate.Diagnostics emits on the "HotChocolate.Diagnostics" ActivitySource,
    // which ServiceDefaults subscribes to. Every option below is set explicitly, even
    // where it matches the package default, because each one is a §15 decision and a
    // future version changing a default must not quietly change what leaves this
    // process. What each one prevents:
    //
    // * RequestDetails: the package default is Id | Hash | OperationName | Extensions.
    //   Document and Variables are excluded (they would carry the raw query text and
    //   raw variable JSON - a search query, a page body on a mutation, both squarely
    //   "page content" and "search query text" under §15). Extensions is ALSO excluded,
    //   which the default does not do: it is written verbatim as
    //   request.Extensions.RootElement.ToString(), and it is a client-controlled bag
    //   this server does not define the contents of. Id/Hash/OperationName remain -
    //   a document hash and the frontend's own operation name are how you find a slow
    //   query without seeing what it asked for.
    // * IncludeDocument: false. The parsed document is the query text by another route,
    //   and a query can embed a literal inline rather than as a variable.
    // * IncludeDataLoaderKeys: false. Keys are page ids - identifiers §15 permits - but
    //   the batch SIZE is what diagnoses an N+1, and the keys add nothing to that.
    // * MaxErrorEvents: 0. GraphQL error events carry graphql.error.message verbatim,
    //   and this schema's own errors include StaleRevisionError, which carries the
    //   page's latest title and content. graphql.error.count and error.type survive at
    //   0, which is exactly the "error rates" §15 asks telemetry to provide; the audit
    //   log (§7) remains the record of what actually happened, and operator-facing
    //   detail goes to logs.
    // * IncludeOperationNameInSpanName: false (the default) - span names must stay
    //   low-cardinality.
    //
    // Scopes: everything except ResolveFieldValue. Per-field spans would be one span per
    // resolved field - a page tree query alone would emit hundreds - and object-level
    // authorization means every Page field is resolved through the same service anyway,
    // so the per-field breakdown restates what the operation-level spans already show.
    // DataLoaderBatch IS kept: PageByIdDataLoader is documented as deduping and
    // parallelizing rather than truly batching (README), and batch-size spans are the
    // only direct measurement of that. EnableResolveFieldValue = false additionally
    // skips the resolver hook entirely rather than starting and discarding activities.
    .AddInstrumentation(o =>
    {
        o.Scopes = ActivityScopes.All & ~ActivityScopes.ResolveFieldValue;
        o.EnableResolveFieldValue = false;
        o.RequestDetails = RequestDetails.Id | RequestDetails.Hash | RequestDetails.OperationName;
        o.IncludeDocument = false;
        o.IncludeDataLoaderKeys = false;
        o.IncludeOperationNameInSpanName = false;
        o.MaxErrorEvents = 0;
    });

var app = builder.Build();

// --- EF Core migrations on startup: config-gated, not unconditional (design.md §15) ---
// Safe only at one replica; on k3s two pods racing MigrateAsync() at startup is
// a corruption risk, so migrations move to a Job/init-container that runs to
// completion before the Deployment rolls. Database:MigrateOnStartup defaults to
// true (appsettings.json) because local/Aspire dev is always single-instance;
// production sets it false via Database__MigrateOnStartup and applies
// migrations through a separate entrypoint instead.
//
// Also skipped for `dotnet run -- schema export` (and any other HotChocolate
// CLI command): RunWithGraphQLCommandsAsync below decides server-vs-command
// mode from the same `args`, but only *after* everything above it in this file
// has already run — without this guard, exporting the schema would try to
// open a SQL Server connection that (in that workflow) has no reason to exist.
var migrateOnStartup = builder.Configuration.GetValue("Database:MigrateOnStartup", defaultValue: true);
if (migrateOnStartup && !args.IsGraphQLCommand())
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    await migrationScope.ServiceProvider.GetRequiredService<RocketWikiDbContext>()
        .Database.MigrateAsync();
}

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseAuthorization();

// JIT provisioning (design.md §11.3) must run before any resolver/route that
// might call IPageService or IAuditSink. AuditContext no longer needs its own
// middleware here - ICurrentAuditContextAccessor computes it on demand from
// HttpContext (see its own doc for why a middleware-set value didn't work).
app.UseMiddleware<JitUserProvisioningMiddleware>();

app.MapGraphQL();

// design.md §8/§10: attachment binary over plain HTTP, same identity/authorization/
// audit pipeline as GraphQL (see AttachmentEndpoints's own doc). No presigned URLs.
app.MapAttachmentEndpoints();

// design.md §8: MCP at /mcp — anonymous requests are rejected by the endpoint's
// authorization policy before any tool code runs (McpServerConfiguration).
app.MapRocketWikiMcp();

// design.md §8: one hub for both durable per-user notifications and ephemeral
// page-scoped presence (see NotificationsHub's own doc for why one hub, not two).
app.MapHub<NotificationsHub>("/hubs/notifications");

// Also enables `dotnet run -- schema export --output schema.graphql` (design.md §8:
// SDL is checked in and code/file drift is a later CI gate).
await app.RunWithGraphQLCommandsAsync(args);

// Top-level statements generate an internal `partial class Program`; this
// declaration merges with it to make the type public, which is all
// WebApplicationFactory<Program> (RocketWiki.Api.Tests) needs to reach it from
// another assembly. Standard ASP.NET Core testing pattern — no behavior change.
public partial class Program;
