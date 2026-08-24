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
//     exist but are unmaintained/version-mismatched against Aspire 13.5.1), so it's
//     wired by hand: explicit S3 (9000) and console (9001) endpoints, dev-only root
//     credentials, and the `server /data --console-address :9001` command.
var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql")
    .WithDataVolume()
    .AddDatabase("rocketwiki");

var minio = builder.AddContainer("minio", "minio/minio")
    .WithVolume("minio-data", "/data")
    .WithHttpEndpoint(targetPort: 9000, name: "http")   // S3 API
    .WithHttpEndpoint(targetPort: 9001, name: "console") // MinIO console (dev only)
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithArgs("server", "/data", "--console-address", ":9001");

// Dev-only convenience: a self-hosted draw.io (diagrams.net) instance for the
// SPA's embedded diagram editor. Hand-wired like MinIO above (no Aspire hosting
// package). Nothing references it yet — the Vite app isn't wired into the
// AppHost (see the TODO below), so set VITE_DRAWIO_URL to this container's
// endpoint by hand (web/.env.example). Production points at its own in-network
// instance instead (design.md §15: the editor URL must never leave the
// boundary). Tag pinned deliberately; the standing caveat (§16) applies — no
// container runtime has ever run this, so it is config-reviewed, not verified.
builder.AddContainer("drawio", "jgraph/drawio", "31.3.2")
    .WithHttpEndpoint(targetPort: 8080, name: "http");

// Dev-only Keycloak instance. Production points RocketWiki.Api at an existing
// realm via configuration instead (design.md §15 "Production"). The `rocketwiki`
// realm — clients, groups, protocol mappers for `groups`/`nationality`, and dev
// users covering the rule engine's edge cases — is seeded from ./keycloak on
// every fresh start (see keycloak/README.md for exactly what's in it, why, and
// what production needs to reproduce by hand). AddKeycloakContainer always
// passes --import-realm, so this is a no-op when the folder is empty and a real
// import once it isn't.
var keycloak = builder.AddKeycloakContainer("keycloak")
    .WithDataVolume()
    .WithImport(Path.Combine(builder.AppHostDirectory, "keycloak"), isReadOnly: true);

// External OpenAI-compatible embeddings endpoint (design.md §9.4) — always a
// connection string, never a container Aspire runs, in every environment.
var embeddings = builder.AddConnectionString("embeddings");

// External OpenAI-compatible chat endpoint for "ask the wiki" (design.md §9) —
// same rules as embeddings: connection string only, in-network by §9.4's boundary
// requirement, fail-closed absent (askWiki answers NOT_CONFIGURED when unset).
// Typically the same gateway as embeddings serving a second model.
var assistant = builder.AddConnectionString("assistant");

var api = builder.AddProject<Projects.RocketWiki_Api>("api")
    .WithReference(sql)
    .WithReference(minio.GetEndpoint("http"))
    .WithReference(keycloak)
    .WithReference(embeddings)
    .WithReference(assistant);

// TODO(milestone 0): the Vite app is not wired into the AppHost yet — it runs
// standalone via `npm run dev` (see DEVELOPING.md). Uncomment once confirmed
// working under the AppHost:
//
//   builder.AddViteApp("web", "../../web").WithReference(api);
//
// Requires the Aspire.Hosting.JavaScript package (referenced in
// RocketWiki.AppHost.csproj).

builder.Build().Run();
