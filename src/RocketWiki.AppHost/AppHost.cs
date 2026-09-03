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
var builder = DistributedApplication.CreateBuilder(args);

// Dev-only MinIO credentials and bucket name. Declared once because three places
// have to agree on them: the container's root user, the bucket the container
// pre-creates, and the API's FileStorage:S3 configuration.
const string MinioRootUser = "minioadmin";
const string MinioRootPassword = "minioadmin";
const string MinioBucket = "rocketwiki";

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
// package). Nothing references it yet — the Vite app isn't wired into the
// AppHost (see the TODO below), so set VITE_DRAWIO_URL to this container's
// endpoint by hand (web/.env.example). Production points at its own in-network
// instance instead (design.md §15: the editor URL must never leave the
// boundary). Tag pinned deliberately; the standing caveat (§16) applies — no
// container runtime has ever run this, so it is config-reviewed, not verified.
// NOTE: nothing consumes this endpoint automatically. The SPA reads
// VITE_DRAWIO_URL, which is not wired from here, and Aspire assigns this container a
// DYNAMIC host port — so the fixed localhost:8080 suggested in web/.env.example is
// wrong for any given run. Read the assigned port off the Aspire dashboard and set
// VITE_DRAWIO_URL to it, or the diagram editor silently fails to load.
builder.AddContainer("drawio", "jgraph/drawio", "31.3.2")
    .WithHttpEndpoint(targetPort: 8080, name: "http");

// Dev-only Keycloak instance. Production points RocketWiki.Api at an existing
// realm via configuration instead (design.md §15 "Production"). The `rocketwiki`
// realm — clients, groups, protocol mappers for `groups`/`nationality`/
// `clearance`/`fruit`, and dev users covering the rule engine's and the marking
// gates' edge cases — is seeded from ./keycloak on every fresh start (see
// keycloak/README.md for exactly what's in it, why, and what production needs
// to reproduce by hand). AddKeycloakContainer always
// passes --import-realm, so this is a no-op when the folder is empty and a real
// import once it isn't.
var keycloak = builder.AddKeycloakContainer("keycloak")
    .WithDataVolume()
    .WithImport(Path.Combine(builder.AppHostDirectory, "keycloak"), isReadOnly: true);

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
var embeddings = builder.AddOptionalConnectionString("embeddings");
var assistant = builder.AddOptionalConnectionString("assistant");

var api = builder.AddProject<Projects.RocketWiki_Api>("api")
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
    // categories the marking control offers and a space admin can grant. FRUIT is
    // gated by a Keycloak attribute — the realm in ./keycloak emits a `fruit`
    // claim, `yes` for the users who are eligible (see keycloak/README.md) — and
    // REGION has no claim, so everyone is eligible and the space grant alone
    // decides. It is the same catalog every test tier fixes on, and that is
    // exactly why it lives here and not in appsettings.Development.json:
    // WebApplicationFactory runs the API as Development and would silently
    // inherit the categories, while the integration tests configure their own
    // catalog on purpose. Unset means NO categories (docs/CONFIGURATION.md
    // "Protective markings"), so nothing outside `aspire run` picks these up.
    // REGION's claim name is stated empty rather than omitted so the rendered
    // environment says "everyone is eligible" out loud; absent and empty mean
    // the same thing to the API.
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Name", "FRUIT")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Description", "Fruit programme compartments")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__ClaimName", "fruit")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Values__0", "APPLE")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__0__Values__1", "BANANA")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Name", "REGION")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Description", "Regional releasability")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__ClaimName", "")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Values__0", "NORTH")
    .WithEnvironment("ProtectiveMarking__SelectorCategories__1__Values__1", "SOUTH")
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

// TODO(milestone 0): the Vite app is not wired into the AppHost yet — it runs
// standalone via `npm run dev` (see DEVELOPING.md). Uncomment once confirmed
// working under the AppHost:
//
//   builder.AddViteApp("web", "../../web").WithReference(api);
//
// Requires the Aspire.Hosting.JavaScript package (referenced in
// RocketWiki.AppHost.csproj).

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
