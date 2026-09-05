using HotChocolate.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RocketWiki.Api.Assistant;
using RocketWiki.Api.Attachments;
using RocketWiki.Api.Audit;
using RocketWiki.Api.Avatars;
using RocketWiki.Api.Embeddings;
using RocketWiki.Api.Emojis;
using RocketWiki.Api.GitLab;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Api.Markings;
using RocketWiki.Api.Mcp;
using RocketWiki.Api.RealTime;
using RocketWiki.Core.Services;
using RocketWiki.Data;
using RocketWiki.Data.Services;
using RocketWiki.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// design.md §12: this instance's identity, read up-front because both the DbContext
// options below and the mutation services further down need it. See the "Page
// services" section for the singleton/service wiring.
var localInstanceId = builder.Configuration["Instance:Id"] ?? "standalone";

// --- Protective-marking selector catalog (design.md §21.15) ---
// One validated vocabulary for the whole process: registered as a singleton for the
// GraphQL vocabulary query, the principal builder and every formatter, and stamped into
// the DbContext options (ConfigureDbContext, below the Aspire registration it layers
// onto) so every gate the data layer runs reads the same object. Invalid configuration
// fails the host at start (ValidateOnStart) — see ProtectiveMarkingConfiguration.
builder.AddRocketWikiProtectiveMarking();

// --- Database (design.md §14/§15) ---
// Official Aspire client integration: wires RocketWikiDbContext to the
// "rocketwiki" connection string the AppHost injects via service discovery
// (the database resource name in AppHost.cs), plus a health check, retry, and
// OpenTelemetry for free — the idiomatic Aspire path rather than hand-rolled
// AddDbContext/UseSqlServer. UseLocalInstanceId stamps the instance id into the
// context options (per-deployment config, pool-safe) so the sync outbox writer can
// verify a space is native before journaling it — design.md §12's "enforced rather
// than assumed" follow-up; see LocalInstanceDbContextOptionsExtension. The selector
// catalog is stamped the same way (SelectorCatalogDbContextOptionsExtension), by
// AddRocketWikiProtectiveMarking above, through the provider-aware hook.
builder.AddSqlServerDbContext<RocketWikiDbContext>("rocketwiki",
    configureDbContextOptions: options => options.UseLocalInstanceId(localInstanceId));

// --- File storage (design.md §10) ---
builder.Services.AddFileStorage(builder.Configuration);

// --- Binary-upload caps (design.md §10/§19) ---
// Three sibling options families for the three binary routes, all bound and validated
// the same way: AddOptions().Bind().ValidateDataAnnotations().ValidateOnStart(), so a
// misconfigured cap (zero, negative, unparseable) fails the host at boot rather than
// every upload at runtime. Every default satisfies its own annotations, which is what
// keeps "no configuration at all" a working state (the API test factory boots on
// nothing but a connection string and proves it).
//
// The one declared attachment size limit (Attachments:MaxSizeBytes, default 100 MiB
// to match the nginx cap in front of the API) — enforced by the upload route before
// any blob or row is written; see AttachmentOptions/AttachmentEndpoints. Kestrel's
// global 30 MB request-body default stays for every other route (GraphQL bodies are
// small); the upload route alone re-derives its per-request cap from this value.
builder.Services.AddOptions<AttachmentOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Profile pictures: Avatars:MaxSizeBytes (5 MiB default) and the fail-closed
// Avatars:GravatarEndpointEnabled flag (default false — see AvatarOptions for why
// an unauthenticated endpoint must be an operator's explicit opt-in).
builder.Services.AddOptions<AvatarOptions>()
    .Bind(builder.Configuration.GetSection(AvatarOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Custom emojis: Emojis:MaxSizeBytes (256 KiB default), bound here beside its two
// siblings rather than hand-bound per request inside the handler.
builder.Services.AddOptions<EmojiOptions>()
    .Bind(builder.Configuration.GetSection(EmojiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Singleton: stateless, and it owns the process-wide capped MemoryAllocator that
// bounds what decoding untrusted uploads can cost (ImageSharpAvatarProcessor).
builder.Services.AddSingleton<IAvatarImageProcessor, ImageSharpAvatarProcessor>();
builder.Services.AddScoped<IUserAvatarService, UserAvatarService>();

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
// request, never from the local User mirror. PrincipalBuilder is the one claim-to-
// Principal mapping, shared by the request accessor and the SignalR hub; a static with
// a fixed claim list (groups, nationality), so nothing to register. It was briefly a
// singleton carrying the selector catalog, while configured selector claims were mapped
// too - see its doc for why that went.
builder.Services.AddScoped<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>();

// design.md §6.6: the rule builder's group picker, accumulated from observed logins by
// JitUserProvisioningMiddleware. Singleton because its whole cost model is a process-wide
// memo of names already known to exist — see KnownGroupRecorder. It holds no request
// state and never participates in an authorization decision.
builder.Services.AddSingleton<KnownGroupRecorder>();

// design.md §6.5: Keycloak's realm-level "admin" role, surfaced via the "roles"
// protocol mapper (rocketwiki-realm.json) — deliberately not part of the ABAC
// Principal above (EffectivePermissionCalculator has no notion of "instance admin").
builder.Services.AddScoped<IInstanceRoleAccessor, InstanceRoleAccessor>();

// --- Page services (design.md §6.7/§8) ---
// PageService needs the local InstanceId (read at the top of this file, where the
// DbContext options also consume it) to tell native spaces from replicas (design.md
// §12). Same value as a DI-visible singleton, for resolvers that need to distinguish
// native from replica or report it (Query.SyncStatus) rather than construct services
// with it.
builder.Services.AddSingleton(new InstanceIdentity(localInstanceId));
builder.Services.AddScoped<IPageReadService, PageReadService>();
// Analytics reads through IPageReadService rather than the DbContext for its
// visible-page set, so the §21 marking gate and §6.4 restrictions are the ones
// already enforced everywhere else rather than a second copy in an aggregation.
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
// Viewer-relative permission facts, the §6.6 inspector, and the manage-gated
// restriction listing. Needs the local InstanceId (unlike IPageReadService) because
// canEdit/canComment honor the replica invariant (design.md §12).
builder.Services.AddScoped<IPagePermissionReadService>(sp =>
    new PagePermissionReadService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<IPageService>(sp => new PageService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<ICommentService>(sp => new CommentService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
builder.Services.AddScoped<ILabelService>(sp => new LabelService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));

// --- Page properties (design.md §20) — admin-defined key registry, per-page values.
// Needs the local InstanceId like every other page-mutation service: setting or
// removing a value is a page mutation and sits beneath the replica invariant (§12).
builder.Services.AddScoped<IPagePropertyService>(sp =>
    new PagePropertyService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));

// --- Protective markings (design.md §21) — the classification that gates canView.
// Needs the local InstanceId for the same reason: re-marking a page is a page mutation
// and sits beneath the replica invariant (§12). There is no *gated* read service — a
// marking is resolved off a Page that already passed canView.
builder.Services.AddScoped<IPageMarkingService>(sp =>
    new PageMarkingService(sp.GetRequiredService<RocketWikiDbContext>(), localInstanceId));
// The DISPLAY-side batch read (design.md §21.13) — markings of pages the caller has
// already been permitted to see, for badges, MCP payloads and aggregate labels. It makes
// no access decision and returns none; enforcement reads markings through
// PermissionContextLoader inside the calculator, and these two paths stay separate on
// purpose. See IPageMarkingReader's doc.
builder.Services.AddScoped<IPageMarkingReader, PageMarkingReadService>();

// --- Custom emojis (design.md §19) — the admin-curated :name: registry over the same
// DbContext + IFileStorage as attachments. Instance-local, never synced, so no
// InstanceId is needed and the plain type registration suffices.
builder.Services.AddScoped<ICustomEmojiService, CustomEmojiService>();

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
// the FTS path is CI-verified by the Testcontainers tier, design.md §14 tier 3).
// Hybrid keyword+vector RRF when the embedding pipeline below is configured; the
// generator/options parameters default to null otherwise and search is keyword-only.
builder.Services.AddScoped<ISearchService, SearchService>();

// --- RQL, the page query language (design.md §22) ---
// Parsing/validation/printing is pure and lives in RocketWiki.Core; this is the
// execute-and-permission-filter half. TimeProvider is injected (Program registers
// TimeProvider.System below) so now() resolves from one clock read per execution and
// tests can pin the instant.
builder.Services.AddScoped<IPageQueryService>(sp =>
    new PageQueryService(sp.GetRequiredService<RocketWikiDbContext>(), sp.GetRequiredService<TimeProvider>()));

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

// --- GitLab integration (design.md GitLab section) ---
// Per-user encrypted PAT storage + typed REST v4 client. GitLab:BaseUrl is
// fail-closed like the OTLP endpoint (§15): unset means the feature is absent and
// every GitLab field answers NOT_CONFIGURED — no default, no fallback. Every fetch
// runs under the calling user's own stored token; no service account exists.
builder.AddRocketWikiGitLab();

// --- "Ask the wiki" assistant (design.md §9, resolving §17's assistant bullet) ---
// RAG over the permission-filtered hybrid index: retrieval under the caller's own
// principal (ISearchService + IPageReadService — answers only from pages the asker
// can view, by construction), generation at the OpenAI-compatible chat endpoint from
// the Aspire "assistant" connection string / Ai:ChatModel. Fail-closed like GitLab
// and the embeddings endpoint: unconfigured means askWiki answers NOT_CONFIGURED and
// no client exists. See AssistantConfiguration for config precedence and the
// telemetry decision; AskWikiService for the enforcement story.
builder.AddRocketWikiAssistant();

// --- Real-time: SignalR (design.md §8) ---
// design.md §15: a Redis backplane is needed once replicas > 1; at one replica (today)
// the default in-memory backplane is correct and this is a real decision to revisit at
// deployment time, not a gap in this wiring.
// Bound here (not via IOptions) because the hub's transport limit below must be
// derived from the same values before the container is built. The IOptions
// registration further down is what validates them: a bad cap fails the host at
// start, so a transport limit derived from one never survives to serve traffic.
var coEditCaps = builder.Configuration.GetSection(CoEditOptions.SectionName).Get<CoEditOptions>() ?? new CoEditOptions();
builder.Services
    .AddSignalR(o =>
    {
        o.EnableDetailedErrors = builder.Environment.IsDevelopment();
        // SignalR's default MaximumReceiveMessageSize is 32 KB - fine for presence
        // frames, far too small for co-editing's Yjs payloads (a seed update or a
        // reseed snapshot is a whole document, design.md §8). The transport limit is
        // derived from the application-level caps in CoEditOptions plus framing
        // slack, so the application caps (which drop oversized messages with
        // telemetry) fire before the transport kills the connection - the transport
        // limit is the backstop, not the policy.
        o.MaximumReceiveMessageSize =
            Math.Max(Math.Max(coEditCaps.SnapshotMaxBytes, coEditCaps.UpdateMaxBytes), 32 * 1024) + 64 * 1024;
    })
    .AddMessagePackProtocol();

// Singleton: ephemeral, in-memory connection/presence state that must be shared across
// every request and Hub invocation, not scoped per-request the way RocketWikiDbContext
// is (design.md §8: presence "no table, no audit rows" — this IS the only copy).
builder.Services.AddSingleton<IRealtimeConnectionRegistry, RealtimeConnectionRegistry>();
builder.Services.AddSingleton<IPresenceRuleChangeNotifier, PresenceRuleChangeNotifier>();

// --- Co-editing (design.md §8 CRDT co-editing, resolving §17's open bullet) ---
// Relay-only edit sessions over the same hub: opaque Yjs updates + a session-scoped
// update log, in-memory exactly like presence (gone on restart — the authoritative
// write stays updatePageContent). See NotificationsHub.EditSessions.cs for the full
// decision note. Options carry the sanity caps; TimeProvider makes the empty-session
// grace sweep deterministic in tests.
builder.Services.AddOptions<CoEditOptions>()
    .Bind(builder.Configuration.GetSection(CoEditOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEditSessionRegistry, EditSessionRegistry>();
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
    // Nitro, Hot Chocolate's built-in GraphQL IDE, served at /graphql to a browser.
    // Set EXPLICITLY rather than left to the package default, and off outside
    // Development, because the default is not a decision anyone here made: this
    // instance holds classified content, and an IDE is a schema browser plus a query
    // console handed to whoever can reach the endpoint. In Development that is exactly
    // what you want; in production it is a starting point.
    //
    // It does not add authorization of its own and does not need to - the IDE is static
    // UI, and every query it sends travels the same authenticated path as the SPA's.
    // What it discloses beyond that is the SCHEMA - and that is now closed too, below.
    .ModifyServerOptions(o =>
    {
        o.Tool.Enable = builder.Environment.IsDevelopment();
        o.Tool.Title = "RocketWiki API";
    })
    // Introspection: on in Development, OFF everywhere else. This was the open decision
    // the Nitro comment above deferred; it is made here, in the same shape and for the
    // same reason.
    //
    // /graphql is reachable unauthenticated (the pipeline authenticates the PRINCIPAL,
    // not the endpoint), and introspection answers without one. What it hands over is
    // structure, never content - an unauthenticated introspection response cannot name a
    // page, a space or a marking value, and every actual read still returns empty/absent
    // (design.md §6.7). So this is defence in depth, not a disclosure fix, and it is
    // ranked that way: what it removes is a map. On an instance whose schema carries
    // field names like `eyesOnly` and `protectiveMarking`, a free, unauthenticated,
    // machine-readable inventory of every query, mutation, argument and enum value is a
    // reconnaissance convenience with no operational purpose in production.
    //
    // Nothing legitimate needs it there:
    //   * the SPA's codegen reads the checked-in schema.graphql, never a live endpoint
    //     (web/codegen.ts), and that file is pinned to the code by SchemaExportTests;
    //   * `dotnet run schema export` builds the schema in-process;
    //   * __typename is NOT introspection and keeps working, which matters because
    //     urql's cache invalidation is keyed on it.
    //
    // Development keeps it because Nitro is unusable without it, and Development is
    // exactly where a schema browser is the point.
    .DisableIntrospection(disable: !builder.Environment.IsDevelopment())
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
// Also skipped for `dotnet run schema export` (and any other HotChocolate
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

// Profile pictures over the same binary-HTTP surface — self-only set/clear, the
// authenticated per-user GET, and the anonymous (flag-gated, default off)
// Gravatar-protocol GET /avatar/{hash}. See AvatarEndpoints for the trust tiers.
app.MapAvatarEndpoints();
// --- Custom emojis: admin-curated :name: registry, instance-local (never synced) ---
// Binary routes on the attachment pattern (admin-only POST/DELETE, authenticated GET
// with ETag/304; emoji.created/.deleted audited via the domain-event pipeline). Its
// options and service are registered above with the other features' — image
// normalization (SixLabors.ImageSharp, decode-limited + re-encoded) stays behind this
// call. See CustomEmojiEndpoints.
app.MapCustomEmojiEndpoints();

// design.md §8: MCP at /mcp — anonymous requests are rejected by the endpoint's
// authorization policy before any tool code runs (McpServerConfiguration).
app.MapRocketWikiMcp();

// design.md §8: one hub for both durable per-user notifications and ephemeral
// page-scoped presence (see NotificationsHub's own doc for why one hub, not two).
app.MapHub<NotificationsHub>("/hubs/notifications", options =>
{
    // design.md §6.1/§8: the hub builds its ABAC Principal from the token at CONNECT and
    // keeps it for the connection's life — send-time canView, the presence sweep and the
    // co-edit sweep all evaluate against that one snapshot. Without this, a connection
    // outlived its token indefinitely: a principal whose `groups` or `nationality` had
    // been revoked in Keycloak kept authorizing presence membership, notification titles
    // and CRDT relay for as long as the socket stayed open, and the rule-change sweep
    // could not compensate because it re-evaluates rules against that same stale
    // principal — attribute-side revocations were outside its reach entirely.
    //
    // CloseOnAuthenticationExpiration closes the connection when the token expires, so the
    // client reconnects and rebuilds the principal from a fresh one. That bounds staleness
    // to the token lifetime, which is the same bound §6.1 already accepts for every other
    // surface ("a change in Keycloak takes effect on the user's next token refresh").
    // It does not make revocation instant, and nothing here claims it does.
    options.CloseOnAuthenticationExpiration = true;
});

// Also enables `dotnet run schema export --output schema.graphql` (design.md §8:
// SDL is checked in and code/file drift is a later CI gate).
await app.RunWithGraphQLCommandsAsync(args);

// Top-level statements generate an internal `partial class Program`; this
// declaration merges with it to make the type public, which is all
// WebApplicationFactory<Program> (RocketWiki.Api.Tests) needs to reach it from
// another assembly. Standard ASP.NET Core testing pattern — no behavior change.
public partial class Program;
