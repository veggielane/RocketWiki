using Microsoft.Extensions.Configuration;

// RocketWiki AppHost — the system topology in code (design.md §15).
//
// Deviations from the design.md §15 snippet (API shape only; intent unchanged):
//   - No official Microsoft Aspire hosting integration exists for Keycloak.
//     `AddKeycloak(...)` in design.md is illustrative; the actual extension method
//     from the leading community package (Keycloak.AuthServices.Aspire.Hosting by
//     NikiforovAll) is `AddKeycloakContainer(...)`.
//   - `WithDataVolume()` is a named-resource convenience (SqlServer, Postgres, Redis,
//     etc.) that generic `AddContainer` resources don't get. MinIO uses the generic
//     `.WithVolume(name, target)` API instead, which is functionally identical.
//   - MinIO has no dedicated Aspire hosting package either (a few third-party ones
//     exist but are unmaintained/version-mismatched against Aspire 13.5.x), so it's
//     wired by hand: explicit S3 (9000) and console (9001) endpoints, dev-only root
//     credentials, and the `server /data --console-address :9001` command (wrapped
//     in a shell so the bucket directory exists first — see below).
//   - The API and the SPA are CONTAINERS built from the repo-root Dockerfiles
//     (`AddDockerfile`), not `AddProject` / `AddViteApp`. §15 says the AppHost
//     "serves both local development and the deployment artifacts", and the
//     images ARE the deployment artifacts: running them here means every start
//     exercises Dockerfile.api, Dockerfile.web and the nginx template the k3s
//     chart ships, rather than a Kestrel process and a Vite dev server that only
//     resemble them. The price is the inner loop — a code change is an image
//     rebuild (layer-cached: a source-only change re-runs publish / `vite build`
//     and nothing before it) and there is no debugger to attach. The fast paths
//     are unchanged and still point at this stack: `npm run dev` proxies to the
//     API's pinned host port, and `dotnet run --project src/RocketWiki.Api` is the
//     container-free backend loop (DEVELOPING.md).
var builder = DistributedApplication.CreateBuilder(args);

// Dev-only MinIO credentials and bucket name. Declared once because three places
// have to agree on them: the container's root user, the bucket the container
// pre-creates, and the API's FileStorage:S3 configuration.
const string MinioRootUser = "minioadmin";
const string MinioRootPassword = "minioadmin";
const string MinioBucket = "rocketwiki";

// The browser-facing addresses, pinned. Everything the BROWSER talks to needs a
// stable host port, because something that cannot be told the port at run time
// has already been told it: the realm's redirect URIs and web origins
// (keycloak/rocketwiki-realm.json) name the SPA's origin, and the SPA's Keycloak
// authority and draw.io URL are Vite build-time constants baked into the web
// image's bundle (the build args below; Dockerfile.web's header explains why
// there is no run-time alternative). Keycloak's own 8080 is pinned by
// AddKeycloakContainer. Each of these is a fact some other file already states,
// so it is declared exactly once here and referenced everywhere else.
const int WebPort = 5173;              // the port the realm was written for (it was the Vite dev server's)
const string KeycloakFrontendUrl = "http://localhost:8080";
const string KeycloakRealm = "rocketwiki";
const int DrawioPort = 8090;           // not 8080: that is Keycloak's
// Plain strings, assembled once. WithEnvironment has an overload that takes an
// interpolated string as a ReferenceExpression (for endpoint holes), so a
// `$"..."` with an int in it does not compile at the call site; these are also
// each used twice (build arg and CSP), which is the point.
var keycloakAuthority = $"{KeycloakFrontendUrl}/realms/{KeycloakRealm}";
var drawioUrl = $"http://localhost:{DrawioPort}";
var cspConnectSrc = $"'self' {KeycloakFrontendUrl}";
var cspFrameSrc = $"{KeycloakFrontendUrl} {drawioUrl}";
// The API's host port is pinned for a different reason. No browser uses it (the
// web container's nginx proxies /graphql, /hubs and the binary routes), but
// `npm run dev` does: web/vite.config.ts's proxy target defaults to this port,
// so the Vite inner loop works against this stack with nothing set.
const int ApiPort = 5079;

// The SA password is pinned rather than generated, for the same reason MinIO's
// credentials above are: it is dev-only, and something outside this file has to
// agree with it. That something is `WithDataVolume()` below. MSSQL_SA_PASSWORD is
// read only when the engine initializes a fresh master database - on every later
// start the copy already in the volume wins - so a generated password and a
// persistent volume are quietly incompatible. Aspire caches the generated one in
// user secrets, which hides that until the day the cached value changes; then the
// engine comes up healthy, refuses every `sa` login, and the API waits on it
// forever with the only clue buried in the container's own log. Pinning keeps the
// volume's copy and the connection string the same fact.
var sqlPassword = builder.AddParameter("sql-password", "RocketWiki-dev-sa-1", secret: true);

// AddSqlServer's default image is the STOCK mcr.microsoft.com/mssql/server, which
// this application cannot run on - proven on the first real container run, not by
// reading docs: the default resolved to 2022-latest, and that engine reported
// IsFullTextInstalled = 0 and no `vector` type at all. InitialCreate's CREATE
// FULLTEXT CATALOG dies with error 7609 on the first, and
// AlterPageEmbeddingToNativeVector's vector(1536) has nothing to bind to on the
// second. design.md §2 names SQL Server 2025 as the production engine for exactly
// the second reason. So the same derived image the test tier has always used
// (docker/mssql-fts/Dockerfile - 2025 base plus the mssql-server-fts package) is
// built here too; the daemon caches it after the first run.
var sql = builder.AddSqlServer("sql", sqlPassword)
    .WithDockerfile("../../docker/mssql-fts")
    .WithDataVolume()
    .AddDatabase("rocketwiki");

// `mkdir -p /data/<name>` before starting the server is how a MinIO bucket gets
// pre-created without a second container or an `mc` sidecar: MinIO treats each
// top-level directory of its data dir as a bucket. It has to exist up front
// because S3FileStorage deliberately never creates one (design.md §10 - the
// bucket, its lifecycle policy and its retention are an operator's decision, not
// something an app should conjure on first upload).
var minio = builder.AddContainer("minio", "minio/minio")
    .WithVolume("minio-data", "/data")
    .WithHttpEndpoint(targetPort: 9000, name: "http")   // S3 API
    .WithHttpEndpoint(targetPort: 9001, name: "console") // MinIO console (dev only)
    .WithEnvironment("MINIO_ROOT_USER", MinioRootUser)
    .WithEnvironment("MINIO_ROOT_PASSWORD", MinioRootPassword)
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", $"mkdir -p /data/{MinioBucket} && exec minio server /data --console-address :9001");

// Dev-only convenience: a self-hosted draw.io (diagrams.net) instance for the
// SPA's embedded diagram editor. Hand-wired like MinIO above (no Aspire hosting
// package). The web image bakes VITE_DRAWIO_URL (drawioUrl above) at build
// time, which is why the host port is pinned here rather than left to Aspire. Production points at its own in-network
// instance instead (design.md §15: the editor URL must never leave the
// boundary). Tag pinned deliberately.
builder.AddContainer("drawio", "jgraph/drawio", "31.3.2")
    .WithHttpEndpoint(port: DrawioPort, targetPort: 8080, name: "http");

// Dev-only Keycloak instance. Production points RocketWiki.Api at an existing
// realm via configuration instead (design.md §15 "Production"). The `rocketwiki`
// realm — clients, groups, protocol mappers for `groups`/`nationality`/`roles`
// plus the API audience, and dev users covering the rule engine's and the
// eyes-only caveat's edge cases — is seeded from ./keycloak on every fresh
// start (see keycloak/README.md for exactly what's in it, why, and what
// production needs to reproduce by hand). It carries no clearance and no
// selector-eligibility claim: neither is a token concern (design.md §21).
// AddKeycloakContainer always passes --import-realm, so this is a no-op when
// the folder is empty and a real import once it isn't.
var keycloak = builder.AddKeycloakContainer("keycloak")
    .WithDataVolume()
    .WithImport(Path.Combine(builder.AppHostDirectory, "keycloak"), isReadOnly: true)
    // Two callers, two addresses, one issuer. The browser reaches Keycloak at
    // KeycloakFrontendUrl (Aspire's proxy on the host); the API container reaches
    // it at http://keycloak.dev.internal:8080 on the container network (Aspire
    // names containers `<resource>.dev.internal` there). Left to itself, Keycloak
    // derives the token issuer from whichever Host header a request arrived
    // with, so the tokens the SPA obtains say
    // `iss: http://localhost:8080/realms/...` while the discovery document the API
    // fetches says `issuer: http://keycloak.dev.internal:8080/...` — and the bearer
    // handler validates the one against the other, so every token is refused
    // (IDX10205) for a perfectly good login. Invisible while the API was a host
    // process using the same localhost address the browser does. KC_HOSTNAME
    // fixes the issuer (and every browser-facing URL) to the frontend address;
    // KC_HOSTNAME_BACKCHANNEL_DYNAMIC keeps the server-to-server URLs in that same
    // document (jwks_uri, token endpoint) following the request, so the API is
    // still pointed at an address it can reach. Keycloak only allows the second
    // when the first is a full URL, which it is.
    .WithEnvironment("KC_HOSTNAME", KeycloakFrontendUrl)
    .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true");

// External OpenAI-compatible embeddings endpoint (design.md §9.4) and chat endpoint
// for "ask the wiki" (design.md §9) — always connection strings, never containers
// Aspire runs, in every environment (§9.4's boundary requirement).
//
// Added ONLY when a value is actually configured. AddConnectionString creates a
// parameter Aspire must resolve before anything referencing it can start, and an
// unset one is not a warning — on the first real run it silently held the whole
// `api` resource in a pending state, so the API never launched and no other part of
// the topology could be exercised either. Both features are defined as fail-closed
// when absent (semantic search degrades to keyword-only; askWiki answers
// NOT_CONFIGURED and registers no chat client at all), so "unconfigured" is a
// supported state that must not be able to stop the stack booting. Set them with
// `dotnet user-secrets set ConnectionStrings:embeddings "..."` in this project.
//
// The API is a container now, so a `localhost` in either value means the
// container, not this machine: an endpoint served from this machine (Ollama, LM
// Studio) must be addressed as host.docker.internal.
//
// The connection strings are the Aspire path. The same two endpoints can also be
// configured per feature without one — `Ai__Embeddings__Endpoint` / `__Model` /
// `__ApiKey` / `__Dimensions` and `Ai__Assistant__Endpoint` / `__Model` / `__ApiKey`,
// settable on the api container with WithEnvironment — for the instance whose
// embedding and chat models are not behind one gateway. A connection string
// still wins per value where both are present (docs/CONFIGURATION.md "AI").
var embeddings = builder.AddOptionalConnectionString("embeddings");
var assistant = builder.AddOptionalConnectionString("assistant");

// The API, as the image Dockerfile.api produces — the same artifact the k3s chart
// deploys (deploy/README.md). Context is the repo root because the Dockerfile's
// COPY paths are written against it; Aspire rebuilds on every start and the
// daemon's layer cache decides how much of that is work.
var api = builder.AddDockerfile("api", "../..", "Dockerfile.api")
    .WithHttpEndpoint(port: ApiPort, targetPort: 8080, name: "http")
    // The image bakes the orchestrated defaults (Dockerfile.api): Production, and
    // Database__MigrateOnStartup=false, because two replicas racing MigrateAsync()
    // is design.md §15's corruption risk. This stack is one replica on one
    // developer's machine, so both go back to what `dotnet run` had: Development
    // for Nitro, introspection, /health and plain-HTTP Keycloak metadata
    // (RequireHttpsMetadata is !IsDevelopment), and migrate-on-startup because the
    // alternative — a one-shot container running the bundled /app/efbundle before
    // the API starts — is the chart's Job, and this file is not the chart.
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Database__MigrateOnStartup", "true")
    // Container to container, so Aspire resolves the server as `sql,1433` on the
    // container network rather than the host-side proxy port.
    .WithReference(sql)
    // Injects service-discovery variables (services__minio__http__0). Nothing
    // resolves a services__* name today — the API reads its S3 endpoint from
    // FileStorage:S3 configuration — so this is inert, kept only because it is how
    // the dependency SHOULD be expressed once service discovery has a consumer.
    .WithReference(minio.GetEndpoint("http"))
    .WithReference(keycloak)
    // WithReference on a container resource injects SERVICE DISCOVERY variables
    // (services__keycloak__http__0), but Program.cs derives the JWT authority from
    // GetConnectionString("keycloak") — two different mechanisms, and nothing bridged
    // them. The result was silent and total: options.Authority stayed null, so the
    // bearer handler had no metadata to validate against and rejected every token,
    // leaving `me` reporting isAuthenticated:false for a perfectly good login. Passing
    // the endpoint as the connection string keeps design.md §15's "config by
    // reference" intent and leaves the realm composition in the API where it belongs.
    // From this container that endpoint is http://keycloak.dev.internal:8080 — see
    // KC_HOSTNAME above for why that address and the browser's can share an issuer.
    .WithEnvironment("ConnectionStrings__keycloak", keycloak.GetEndpoint("http"))
    // Dev-only MinIO credentials, matching the container above. Without these the
    // API falls back to the FileSystem provider and the MinIO container is dead
    // weight — which is exactly what the first real run found, and why the S3
    // provider had still never executed a single request against a real endpoint.
    .WithEnvironment("FileStorage__Provider", "S3")
    .WithEnvironment("FileStorage__S3__ServiceUrl", minio.GetEndpoint("http"))
    .WithEnvironment("FileStorage__S3__Bucket", MinioBucket)
    .WithEnvironment("FileStorage__S3__ForcePathStyle", "true")
    .WithEnvironment("FileStorage__S3__AccessKey", MinioRootUser)
    .WithEnvironment("FileStorage__S3__SecretKey", MinioRootPassword)
    // Dev-only protective-marking selector vocabulary (design.md §21.15): the
    // categories, and the values under each, that the marking control offers
    // and a space admin grants from. That is all a category is — vocabulary.
    // Nothing about it is gated by a token claim: a reader sees a page
    // carrying APPLE only when a space access grant that matches them carries
    // APPLE, and the realm in ./keycloak emits no claim named after any of
    // these (there is no ClaimName key any more — see keycloak/README.md). It
    // is the same catalog every test tier fixes on, and that is exactly why
    // it lives here and not in appsettings.Development.json:
    // WebApplicationFactory runs the API as Development and would silently
    // inherit the categories, while the integration tests configure their own
    // catalog on purpose. Unset means NO categories (docs/CONFIGURATION.md
    // "Protective markings"), so nothing outside `aspire run` picks these up.
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Name", "FRUIT")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Description", "Fruit programme compartments")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Values__0", "APPLE")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Values__1", "BANANA")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Name", "REGION")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Description", "Regional releasability")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Values__0", "NORTH")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Values__1", "SOUTH")
    // AddProject wires the dashboard's OTLP endpoint into a project implicitly; a
    // Dockerfile resource gets nothing unless asked. ServiceDefaults switches the
    // exporter on only when OTEL_EXPORTER_OTLP_ENDPOINT is set, so without this
    // line the API's traces, metrics and logs never leave the container and the
    // dashboard's telemetry pages stay empty with no error anywhere.
    .WithOtlpExporter()
    // Readiness, so the web container's WaitFor(api) below means "answering", not
    // "process started". /health is mapped in Development (ServiceDefaults).
    .WithHttpHealthCheck("/health")
    // WithReference wires configuration; it does NOT imply waiting. Without these the
    // API starts the moment its own dependencies are *described*, races SQL Server's
    // boot, and dies — observed, not theorised: the first run that got this far
    // crashed in Migrate() with "Error Number:-2 ... The wait operation timed out"
    // while SQL Server was still starting. Database:MigrateOnStartup runs before the
    // host is listening and has no connection retry, so losing that race is fatal
    // rather than merely slow. SQL Server 2025 with Full-Text Search takes a while to
    // report healthy, which makes the race one the API reliably loses on a cold start.
    .WaitFor(sql)
    // Keycloak is not needed to *boot* (OIDC metadata is fetched lazily on the first
    // authenticated request), but waiting means the first login after `aspire run`
    // works instead of failing against a realm that is still importing.
    .WaitFor(keycloak)
    .WaitFor(minio);

if (embeddings is not null)
{
    api.WithReference(embeddings);
}

if (assistant is not null)
{
    api.WithReference(assistant);
}

// The SPA, as the image Dockerfile.web produces: the Vite build served by nginx,
// which proxies /graphql, /hubs and the binary routes to the API so the bundle's
// same-origin defaults hold (deploy/docker/nginx/default.conf.template). This is
// the one resource a browser is pointed at.
var apiHttp = api.GetEndpoint("http");
builder.AddDockerfile("web", "../..", "Dockerfile.web")
    // Vite inlines VITE_* at build time (Dockerfile.web's header): these three are
    // permanent properties of the image, which is why each is a pinned host-side
    // address from the constants above rather than an endpoint reference. A
    // reference would resolve to the container-network name (`keycloak:8080`),
    // which is right for the API and meaningless to a browser.
    .WithBuildArg("VITE_OIDC_AUTHORITY", keycloakAuthority)
    .WithBuildArg("VITE_OIDC_CLIENT_ID", "rocketwiki-web")
    .WithBuildArg("VITE_DRAWIO_URL", drawioUrl)
    .WithHttpEndpoint(port: WebPort, targetPort: 8080, name: "http")
    .WithExternalHttpEndpoints()
    // host:port on the container network, no scheme — the template supplies
    // `http://`. Derived from the api resource rather than left to the image's
    // `api:8080` default, which is NOT the name Aspire gives the container
    // (`api.dev.internal`): with the default, nginx would 502 every API call.
    .WithEnvironment("API_UPSTREAM", ReferenceExpression.Create($"{apiHttp.Property(EndpointProperty.HostAndPort)}"))
    // The Content-Security-Policy's two deployment-supplied directives (the nginx
    // template's header): connect-src must name the Keycloak origin, because
    // oidc-client-ts fetches discovery, token and userinfo over XHR; frame-src must
    // name it too (silent renew is an iframe) and the draw.io origin (the diagram
    // editor is an iframe). The same addresses as the build args — the same facts,
    // which is the point of stating each once above.
    .WithEnvironment("CSP_CONNECT_SRC", cspConnectSrc)
    .WithEnvironment("CSP_FRAME_SRC", cspFrameSrc)
    .WithHttpHealthCheck("/")
    .WaitFor(api);

builder.Build().Run();

file static class OptionalConnectionStringExtensions
{
    /// <summary>
    /// <see cref="ResourceBuilderExtensions.AddConnectionString(IDistributedApplicationBuilder, string)"/>
    /// only when a value for it exists in the AppHost's own configuration, otherwise
    /// <c>null</c>.
    ///
    /// <para>Aspire treats a connection string as a parameter it has to resolve before any
    /// resource referencing it may start, and an unresolved one produces no error — it
    /// just waits. For a feature that is genuinely optional that is the wrong trade:
    /// leaving RocketWiki's AI endpoints unset should mean "that feature is off",
    /// never "the API does not boot". Registering the resource conditionally is what
    /// makes the AppHost agree with the application's own fail-closed semantics.</para>
    /// </summary>
    public static IResourceBuilder<IResourceWithConnectionString>? AddOptionalConnectionString(
        this IDistributedApplicationBuilder builder, string name) =>
        string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(name))
            ? null
            : builder.AddConnectionString(name);
}
