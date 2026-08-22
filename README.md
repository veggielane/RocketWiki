# RocketWiki

A self-hosted Confluence replacement for an aerospace company with
export-control requirements: WYSIWYG editing over a Markdown storage format, a
.NET/GraphQL backend, and attribute-based access control designed for
"engineering AND nationality NZ or US" style rules.

**This is under active, parallel construction (milestone 0/1 of design.md
§16).** Nothing here has been deployed or run end to end — see
[Current status](#current-status) before assuming anything works.

The source of truth for *why* things are built this way is
[`design.md`](design.md) (architecture, access control, audit, deployment) and
[`data-model.md`](data-model.md) (the concrete EF Core / SQL Server schema).
This file is operational: how to get the code building and running, and what
has and hasn't actually been verified.

## Prerequisites

- **.NET 10 SDK** (developed against 10.0.400).
- **Node.js 24** and npm, for the `web/` frontend (developed against Node
  24.14.1 / npm 11.12.1).
- **A container runtime** (Docker or Podman) is required to run `aspire run` —
  it starts SQL Server, MinIO, and Keycloak as containers. **Not installed in
  the environment this was developed in**, so the full stack has never
  actually been started; see [Current status](#current-status).
- The Aspire CLI and project templates
  (`dotnet tool install -g Aspire.Cli`, `dotnet new install
  Aspire.ProjectTemplates`) if you want to scaffold further, though `aspire
  run`/`aspire publish` also work via the CLI tool once container tooling is
  present.

## Building and testing

```
dotnet build RocketWiki.sln
```

Builds every .NET project in the solution — Aspire AppHost, service defaults,
domain model, EF Core, the API, file storage, and their test projects.

```
dotnet test RocketWiki.sln
```

Runs every test project in the solution: `RocketWiki.Core.Tests` (rule engine,
domain events), `RocketWiki.Data.Tests` (EF model against SQLite),
`RocketWiki.Storage.Tests` (`FileSystemFileStorage` round-trip, overwrite, and
path-traversal safety; DI provider selection), `RocketWiki.Importer.Tests`
(owned by another agent), and `RocketWiki.Api.Tests` (the GraphQL schema-drift
check, the audit-declaration coverage check, and an integration tier that
exercises the real ASP.NET Core + Hot Chocolate pipeline — GraphQL mutations
*and* the plain-HTTP attachment routes — against EF Core on SQLite with a fake
authentication handler standing in for Keycloak — design.md §14's
container-free tier).

To regenerate the checked-in GraphQL SDL after changing the schema:

```
cd src/RocketWiki.Api && dotnet run schema export --output ../../schema.graphql
```

`RocketWiki.Api.Tests` fails the build with a readable diff and this exact
command if `schema.graphql` drifts from the code (design.md §8).

### Frontend

```
cd web
npm install
npm run dev      # Vite dev server
npm run build    # tsc -b && vite build
npm test         # vitest
```

See [`web/README.md`](web/README.md) and `web/package.json` for the rest of
the frontend's own tooling (codegen, lint).

### Running the whole stack

```
cd src/RocketWiki.AppHost
aspire run
```

This is what *should* bring up SQL Server, MinIO, a Keycloak seeded with the
`rocketwiki` dev realm, and the API, with the Aspire dashboard for logs and
traces (design.md §15). **This has never been run** in the environment this
was built in — no container runtime was available. See below for exactly
what that means.

## Project layout

```
RocketWiki/
├── design.md, data-model.md      source of truth — read these first
├── RocketWiki.sln
├── src/
│   ├── RocketWiki.AppHost/        Aspire topology (SQL Server, MinIO, Keycloak,
│   │                              embeddings connection string, the API)
│   ├── RocketWiki.ServiceDefaults/ health checks, OpenTelemetry, resilience
│   ├── RocketWiki.Api/            ASP.NET Core + Hot Chocolate GraphQL, JWT
│   │                              bearer auth, audit-declaration attributes,
│   │                              EF Core + file storage DI wiring,
│   │                              request-scoped AuditContext + JIT user
│   │                              provisioning + IAuditSink (design.md §7/§11)
│   ├── RocketWiki.Storage/        IFileStorage: FileSystem + S3 providers
│   ├── RocketWiki.Core/           domain model, rule engine, domain events
│   ├── RocketWiki.Data/           EF Core, migrations, SQL Server specifics
│   └── RocketWiki.Importer/       Confluence space import pipeline (another
│                                  agent's project — not consumed by the API)
├── tests/
│   ├── RocketWiki.Api.Tests/      schema-drift + audit-coverage checks, plus
│   │                              a SQLite-backed integration tier (§14)
│   ├── RocketWiki.Storage.Tests/  filesystem provider + DI wiring
│   ├── RocketWiki.Core.Tests/     rule engine, domain events (unit)
│   ├── RocketWiki.Data.Tests/     EF model against SQLite (integration)
│   └── RocketWiki.Importer.Tests/ (another agent's project)
└── web/                           Vite + React + TypeScript SPA
```

All twelve .NET projects above are in `RocketWiki.sln`; `RocketWiki.Api`
references `RocketWiki.Core`, `RocketWiki.Data`, and `RocketWiki.Storage`.
The application service layer another agent built
(`RocketWiki.Core/Services/` — `IPageService`, `IPageReadService`,
`ICommentService`, `ILabelService`, `IAttachmentService`,
`IAttachmentReadService`) is now fully consumed: every GraphQL resolver and
mutation, and the two plain-HTTP attachment routes, route through it — never
a raw EF query or an entity's own navigation property, for exactly the
object-level-authorization reasons design.md §6.7/§8 calls for.

## Current status

Being explicit about what has and hasn't been checked, rather than letting
"it builds" stand in for "it works":

**Verified:**
- `dotnet build RocketWiki.sln` / `dotnet test RocketWiki.sln`, run solution-wide,
  reflect whatever every agent's projects look like *right now* — with several
  agents working in parallel, a transient break in one project (most recently,
  `RocketWiki.Importer`) is expected and not a signal the rest is broken. What's
  independently verified, per project this file's author owns: `RocketWiki.Api`
  and `RocketWiki.Api.Tests` build clean and 39/39 tests pass;
  `RocketWiki.Storage` and `RocketWiki.Storage.Tests` build clean and 26/26
  pass. Run each project's own `dotnet test <path>.csproj` rather than trust a
  number written here — it drifts, and a solution-wide run can fail for a
  reason that has nothing to do with the project you actually care about.
- **Request-scoped audit context and JIT user provisioning are real and
  tested**, not just declared: `ICurrentAuditContextAccessor` derives the
  audit channel from the actual request path (unit-tested per path), and
  `JitUserProvisioningMiddleware` upserts the local `User` row from token
  claims on every authenticated request (verified through real HTTP requests
  against the SQLite-backed integration tier, including that the same
  subject upserts rather than duplicates, and that anonymous requests
  provision nothing). `IAuditSink`/`DbAuditSink` (design.md §7's seam for
  reads and denials, which aren't domain events) is implemented and proven
  to fail closed — a request with no resolvable acting user, or no
  `AuditContext` available, throws and writes nothing rather than logging a
  misleadingly anonymous-looking row.
  **Load-bearing finding**: this originally used a middleware
  (`app.UseMiddleware<T>()`) to set state on `RocketWikiDbContext` for
  resolvers to read back, and it silently didn't work — Hot Chocolate
  executes resolvers in a different DI scope than that middleware pipeline,
  so a resolver's injected `RocketWikiDbContext` is a different *instance*
  than any middleware touched. Both `ICurrentAuditContextAccessor` and
  `IActingUserAccessor` now derive their values fresh from `HttpContext` on
  every read instead of relying on scoped-service instance identity to
  survive that boundary. Caught by the integration tier before it ever
  reached anything resembling production; would otherwise have made every
  real GraphQL read and mutation throw once real resolvers existed.
- **Page reads, writes, and object-level authorization are wired and
  adversarially tested** (design.md §6.7, §8): `page(id)`, `pageTree`, and
  `Page.parent`/`children`/`revisions` all route exclusively through
  `IPageReadService` — never a raw EF query or the entity's own navigation
  properties, which `PageType`/`PageRevisionType` explicitly suppress. Six
  mutations (create/update/move/delete/restore/restoreRevision) wrap
  `IPageService`, mapping its typed errors and auditing denials explicitly
  (mutation successes are audited by the domain-event pipeline instead).
  `PageAdversarialLeakTests` builds a tree with a restricted page and
  confirms it's absent — not merely inaccessible — through every path: direct
  query, as a child of a permitted parent, inherited by its own child, via
  revision history, and via tree traversal, while a sibling with no
  restriction and a principal who does satisfy the restriction both resolve
  correctly (so the negatives are a real restriction working, not the
  resolver being broken).
  **Known gap, stated plainly**: denied *reads* aren't audited with a reason
  — `IPageReadService` deliberately returns the same `null` for "doesn't
  exist" and "not permitted" (design.md §6.7), so there is nothing here to
  distinguish which happened. Only `IAuditSink`'s capacity to record a denial
  is proven (directly, in `AuditPipelineTests`); nothing today calls it for a
  read. Mutation denials *are* fully audited, since `PageMutationError`
  carries a reason. (The design call has since been made — §6.7 now specifies
  read services return an internal not-found-vs-denied result that collapses
  to `null` only at the response boundary — but the Core-side change hasn't
  landed yet; the resolver side is wired once it does.)
- **Comments, labels, and attachments are wired end to end** — GraphQL
  mutations (`addComment`/`editComment`/`deleteComment`,
  `createLabel`/`attachLabel`/`detachLabel`, `deleteAttachment`) and reads
  (`Page.comments`, `Page.labels`, `Page.attachments`), each `[AuditAction]`
  declared. None of these carry an authorization rule of their own — they
  inherit the parent `Page`'s canView/canEdit, already adversarially proven —
  so their tests (`CommentAndLabelMutationTests`) are functional
  (happy-path round trips, one genuine denial each) rather than a repeat of
  the adversarial leak suite.
- **The two non-GraphQL attachment routes** (`POST /attachments/{pageId}`,
  `GET /attachments/{id}`, design.md §8/§10) run through the same
  identity/authorization/audit pipeline as GraphQL — no side channel, no
  presigned URLs, ever. `AttachmentEndpointTests` proves: a full
  upload-then-download round trip; upload correctly refused (403, no row
  persisted) without `canEdit`; a fully anonymous upload rejected at the
  ASP.NET Core pipeline level (401) before any handler code runs; an
  attachment on a page the caller can't view returns 404 — identical to one
  that doesn't exist at all, the same "absent, not forbidden" rule as `Page`
  (design.md §6.7); and `AttachmentDownloadResult.BlobMissing` (the row
  exists but the stored object doesn't) surfaces as a structured, logged 500
  — `ProblemDetails` plus a server-side log line an operator can act on —
  never an unhandled exception.
- **`Page.children` is batched with a DataLoader** (`PageByIdDataLoader`) to
  fix the N+1 flagged in the previous round. Honest caveat: `IPageReadService`
  has no batch-fetch method, so this dedupes and parallelizes per request
  rather than reducing to one SQL query — a real improvement, not the full
  fix. A true single-query batch would need a new `IPageReadService` method,
  a Core-side change not requested here.
- The API runs standalone (`dotnet run` from `src/RocketWiki.Api`, outside
  Aspire) and its health endpoints (`/health`, `/alive`) and the `/graphql`
  endpoint (a placeholder `me` query reading claims off the request) actually
  respond correctly.
- **`RocketWikiDbContext` is wired into the API for real** (`AddSqlServerDbContext`,
  the official Aspire client integration, bound to the "rocketwiki" connection
  string) and genuinely exercised: `RocketWiki.Api.Tests`' integration tier
  runs the real GraphQL pipeline against EF Core on SQLite in memory, with a
  fake authentication handler standing in for Keycloak and
  `FileSystemFileStorage` pointed at a temp directory — no containers. This is
  the tier design.md §14 calls the bulk of the suite, and it's green.
- Migrate-on-startup is config-gated (`Database:MigrateOnStartup`, default
  `true`) and confirmed to do the right thing on **both** paths: a normal run
  attempts the migration and fails loudly against an unreachable SQL Server
  (proving it isn't silently skipped), while `dotnet run schema export` skips
  it entirely (proving the schema-export workflow doesn't require a database).
- `RocketWiki.Storage`'s filesystem provider is exercised by real disk I/O in
  its test suite, including seven distinct path-traversal attack strings, all
  correctly rejected.
- The GraphQL schema-drift test and the audit-declaration coverage test are
  each confirmed to actually fail when the condition they guard against is
  deliberately introduced, not just written and left untested against
  themselves.
- `RocketWiki.Core.Tests` and `RocketWiki.Data.Tests` (owned by another
  agent) have passed as part of a solution-wide run in the past — not
  independently reverified by this file's author this round (see the
  solution-wide build/test caveat above); the EF model runs against SQLite in
  that suite too, independently of the API-level integration tier above.

**Explicitly unverified — no container runtime was available:**
- **The entire Aspire container topology.** SQL Server, MinIO, and Keycloak
  have never actually been started by `aspire run`. Resource wiring
  (`WithReference`, service discovery, connection string injection) is
  correct by inspection and compiles, but has not been observed working at
  runtime. Concretely: `RocketWikiDbContext` is proven correct against
  SQLite (see above), but the checked-in EF Core migrations are SQL
  Server-specific and have never actually been applied to a real SQL Server
  — `Database:MigrateOnStartup`'s happy path is unverified even though its
  failure path (an unreachable server) was confirmed to fail loudly rather
  than silently.
- **The Keycloak dev realm import**
  (`src/RocketWiki.AppHost/keycloak/rocketwiki-realm.json`). The JSON is
  syntactically valid and was checked by decompiling the Aspire Keycloak
  hosting package to confirm exactly how `--import-realm` and the bind mount
  work, but no realm has actually been imported, no user has logged in, and
  no token has been decoded to confirm the `groups`/`nationality`/`aud`
  claims land as designed. Targets Keycloak `26.6.1` (the hosting package's
  default image tag) — see that folder's own `README.md` for the full detail
  and what production Keycloak needs to reproduce instead.
- **`RocketWiki.Storage`'s S3 provider** against a real S3-compatible
  endpoint. Only its DI/config wiring is tested; `PutObjectAsync`,
  `GetObjectAsync`, etc. have never run against MinIO or anything else.
- **Denied-read auditing.** `AuditFieldMiddleware` now wires `[AuditAction]`
  fields (`page(id)`, `pageTree`, `Page.content`, `Page.revisions`) to
  `IAuditSink` automatically, deduplicated per request, and this is
  end-to-end tested via real HTTP requests (`PageAdversarialLeakTests`).
  What's still missing is auditing a *denied* read with its reason:
  `IPageReadService` deliberately returns the same `null` for "doesn't
  exist" and "not viewable" (design.md §6.7), so nothing at the API layer
  can tell which happened, or attach a failing-restriction reason, for a
  read specifically. `IAuditSink`'s capacity to record `Outcome.Denied` is
  proven directly (`AuditPipelineTests`); nothing calls it for a read today.
  Closing this would need either a richer `IPageReadService` return type or
  the service auditing its own denials internally, where the reason is
  already computed — a Core-side change, not mine to make unilaterally.
- **The frontend**, beyond the fact that it exists and is a separate agent's
  work in progress — this file makes no claim about `web/`'s state.
- Real login, real page CRUD, real search, real anything involving SQL
  Server or Keycloak issuing a token — none of it exists yet at more than a
  placeholder level (see design.md §16's milestone list for what's next).
