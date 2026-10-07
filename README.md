# RocketWiki

A self-hosted Confluence replacement for an aerospace company with
export-control requirements: WYSIWYG editing over a Markdown storage format, a
.NET/GraphQL backend, and attribute-based access control designed for
"engineering AND nationality NZ or US" style rules.

![Page view — rendered Markdown with custom emojis, labels, watch toggle, and a mirrored-space nav](docs/screenshots/page-view.png)

<details>
<summary>More screenshots: Ask the wiki, search with section-attributed results, and the Settings page</summary>

![Ask the wiki — a cited answer drawing only on pages the asker can view](docs/screenshots/ask.png)

![Search results with heading-path attribution and permission-filtered totals](docs/screenshots/search.png)

![Settings — profile picture upload and the GitLab personal-access-token flow](docs/screenshots/settings.png)

</details>

*Screenshots are real components with staged sample data, captured headlessly
— not a live deployment; see [docs/screenshots/README.md](docs/screenshots/README.md).*

**This is under active, parallel construction.** Every design.md §16
milestone except the Confluence migration trial is implemented and
test-verified (the SQL Server slice on every CI run). The stack has been run
end to end on real containers — see
[Getting started with Docker](#getting-started-with-docker) to do it yourself
— but it has never been *deployed*: the Helm chart and images are authored and
unexercised, and nothing has been applied to a cluster. Read
[Current status](#current-status) before assuming any particular thing works.
To work on it, start with [DEVELOPING.md](DEVELOPING.md).

A browsable version of these documents can be generated from `docs-site/`
(`docs-site/README.md` explains the scheme — edit the canonical files here,
never the site). Run it locally with `npm run dev` in that directory.

**It is no longer published.** The site used to deploy to GitHub Pages on
every push to `main`, which meant a public site describing a private
repository's design — including how its access control and protective
markings work. That deployment has been removed and the Pages site
unpublished. The workflow remains, because it also runs the generator's
tests and a link check that fails on a broken `§`-reference, and nothing in
`ci.yml` does that; it just no longer uploads anything.

The source of truth for *why* things are built this way is
[`design.md`](design.md) (architecture, access control, audit, deployment) and
[`data-model.md`](data-model.md) (the concrete EF Core / SQL Server schema).
This file is operational: how to get the code building and running, and what
has and hasn't actually been verified. Every runtime configuration key — its
default, what *unset* means, and which keys fail closed — is catalogued in
[docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Prerequisites

- **.NET 10 SDK** (developed against 10.0.400).
- **Node.js 24** and npm, for the `web/` frontend (developed against Node
  24.14.1 / npm 11.12.1).
- **Docker** — for the full stack (SQL Server, Keycloak, MinIO, draw.io, and
  the API and web images) and for the SQL Server test tier. Both are
  optional: everything else builds, tests and runs without it. Developed
  against Docker Desktop 4.87.0 (engine 29.7.2, Linux containers). **Give
  Docker Desktop's VM at least 8 GB of memory** (Settings → Resources, or
  `MemoryMiB` in `%APPDATA%\Docker\settings-store.json`): the AppHost builds
  the API and web images inside that VM while SQL Server, Keycloak, MinIO and
  draw.io are already running in it, and on the 2 GB Hyper-V default the
  `npm ci` stage drove the VM into thrashing until the engine stopped
  answering at all — see "When it doesn't come up".
- The Aspire CLI and project templates
  (`dotnet tool install -g Aspire.Cli`, `dotnet new install
  Aspire.ProjectTemplates`) only if you want the `aspire` command itself —
  `dotnet run --project src/RocketWiki.AppHost` needs neither.

## Getting started with Docker

The full stack, from a clean clone to signed in as a real user. This path has
been walked end to end from empty volumes (2026-08-28 — see
[Current status](#current-status)), and the warnings below are the specific
things that cost time when it was.

**1. Trust the ASP.NET dev certificate**, once per machine:

```
dotnet dev-certs https --trust
```

Do this *before* the first `dotnet run`. The Aspire CLI otherwise tries it for
you and sits on a modal OS dialog until someone clicks it — which, from a
script or a terminal you have looked away from, is indistinguishable from a
hung build.

**2. Start the stack:**

```
dotnet run --project src/RocketWiki.AppHost
```

That brings up SQL Server (with a data volume), Keycloak with the `rocketwiki`
dev realm auto-imported, MinIO, a draw.io container for the diagram editor,
and then the API and the SPA — **as the images `Dockerfile.api` and
`Dockerfile.web` build**, the same artifacts the k3s chart deploys, with
connection strings injected and startup ordering handled. The AppHost rebuilds
both images on every start, layer-cached: with nothing changed the whole stack
is answering again well under a minute after launch (48 s measured), and a
source change costs a `dotnet publish` or `npm ci` + `vite build` inside Docker
(a couple of minutes). The Aspire dashboard URL is printed at startup, and
receives all traces, metrics and logs (design.md §15).

**The first run builds three images**: SQL Server from `docker/mssql-fts` (a few
minutes; cached afterwards), then the API and the SPA. The SQL Server one is not
optional and not a preference: Aspire's default `AddSqlServer` image has
neither Full-Text Search nor the `vector` type, so the application cannot run
on it at all.

**3. Open the app** at <http://localhost:5173> once the dashboard shows `web`
healthy. That is the nginx in the web image serving the built SPA and proxying
`/graphql`, `/hubs` (WebSocket), `/attachments`, `/avatars`, `/avatar`,
`/emojis` and `/users` to the API container, which is why the API carries no
CORS policy. The host ports are pinned, not assigned: `5173` for the SPA
(the realm's redirect URIs name it), `8080` for Keycloak, `5079` for the API
and `8090` for draw.io — the SPA's Keycloak authority and draw.io URL are
baked into the image at build time, so they cannot be told a port at run time
(see the constants at the top of `AppHost.cs`).

For frontend work, the Vite dev server is still the fast path, and it points at
this stack with nothing to configure:

```
cd web
npm ci
npm run codegen   # REQUIRED — the typed client is generated from
                  # ../schema.graphql and deliberately not committed
npm run dev       # lands on http://localhost:5174 (5173 is the web container)
```

Its proxy targets the API's pinned port by default, and the realm allows the
`5174` origin — into a *fresh* Keycloak volume; see "When it doesn't come up".
Copy `web/.env.example` to `web/.env.local` for anything beyond the defaults;
every `VITE_*` key is documented there and in
[docs/CONFIGURATION.md](docs/CONFIGURATION.md). Take `VITE_OIDC_AUTHORITY`
from `.env.example` as-is (`http://localhost:8080/realms/rocketwiki`) rather
than reading a port off `docker ps`: Keycloak's *published* container port is
randomised per run; 8080 is Aspire's stable proxy in front of it, and the
issuer in every token is pinned to it (`KC_HOSTNAME` in `AppHost.cs`).

**4. Sign in.** The realm seeds seven users, all with password
`RocketWiki!Dev1`, chosen to cover the rule engine's edge cases rather than to
be a plausible org chart — `alice.engineer` is the ordinary one to start with,
`frank.admin` is the instance admin, and `carol.noattr` deliberately carries no
attribute claims at all (§6.3's fail-closed case). The full table, and what
each proves, is in
[`src/RocketWiki.AppHost/keycloak/README.md`](src/RocketWiki.AppHost/keycloak/README.md).

**5. Optional: the GraphQL IDE.** Nitro is served at the API's `/graphql`
(<http://localhost:5079/graphql> in a browser) in Development — a schema
browser and query console, served from the copy embedded in the Hot Chocolate
package rather than fetched from ChilliCream's CDN, so it works with no route to
the internet. It is switched off in every other environment explicitly, rather
than by relying on the package default, because this application holds
classified content. Queries there travel the same authenticated path the SPA's
do, so an unauthenticated session sees exactly what an unauthenticated SPA
would: absent-shaped empty answers.

### When it doesn't come up

- **`web` sits in `Building` for ten minutes, then `FailedToStart` with
  `Container startup failed: context deadline exceeded` — and meanwhile
  `docker ps` hangs and the API stops answering.** The Docker VM has run out of
  memory: the image build (npm's install and the Vite build are the heavy
  stages) is competing with the running SQL Server, Keycloak, MinIO and
  draw.io for a VM that, on Docker Desktop's Hyper-V default, has 2 GB. Raise
  it to 8 GB (Prerequisites above) and restart Docker Desktop; the ten minutes
  is DCP's build deadline, not the build's real cost.

The next two are "only on a fresh volume" rules, and both look like something
else:

- **`docker volume rm` the Keycloak volume after editing
  `rocketwiki-realm.json`.** The realm imports only into a fresh volume, so
  otherwise the old one persists and your change appears to do nothing.
- **SQL Server's `sa` password lives in the volume, not in config.**
  `MSSQL_SA_PASSWORD` is read only when the engine initializes a new master
  database; after that the volume's copy wins. `AppHost.cs` pins the password
  (`RocketWiki-dev-sa-1`) rather than letting Aspire generate one, because a
  generated password works until the cached value changes and then the stack
  half-starts *forever*: SQL Server reports healthy, the API sits in
  `WaitFor(sql)`, and the only evidence is `Login failed for user 'sa'` inside
  the container log. To fix without losing data, see the `mssql-conf` recipe in
  [DEVELOPING.md](DEVELOPING.md#full-stack-docker--aspire-run).

[DEVELOPING.md](DEVELOPING.md) is the fuller reference — test tiers, the
no-Docker paths, CI, and package upgrades.

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
(the Confluence import pipeline), `RocketWiki.Sync.Tests` (the low→high
bundle export/import CLI, design.md §12), `RocketWiki.SqlServer.Tests` (the
Testcontainers tier — without Docker it shows as SKIPPED, visibly and
deliberately; CI's `sqlserver` job runs it for real), and
`RocketWiki.Api.Tests` (the GraphQL schema-drift
check, the audit-declaration coverage check, and an integration tier that
exercises the real ASP.NET Core + Hot Chocolate pipeline — GraphQL mutations
*and* the plain-HTTP attachment routes — against EF Core on SQLite with a fake
authentication handler standing in for Keycloak — design.md §14's
container-free tier).

Every project also carries telemetry tests (design.md §15): `MetricCollector`
and `ActivityListener` coverage that the custom instruments emit with the tags
claimed, plus `TelemetryHygieneTests` in `RocketWiki.Api.Tests`, which sweeps
every ActivitySource in the process for page content, search text, and
principal attribute values. Those classes sit in a non-parallelizable xUnit
collection — an `ActivitySource` and a `Meter` are process-wide, so a listener
in one class otherwise captures whatever a class running in parallel emits.

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
npm run codegen  # REQUIRED first: generates the typed GraphQL client from
                 # ../schema.graphql (deliberately untracked) — nothing
                 # compiles without it
npm run dev      # Vite dev server
npm run build    # tsc -b && vite build
npm test         # vitest
```

See [`web/README.md`](web/README.md) and `web/package.json` for the rest of
the frontend's own tooling (codegen, lint).

### Running without Docker

```
# launchSettings.json is gitignored, so ask for the port the dev proxy expects
ASPNETCORE_URLS=http://localhost:5079 dotnet run --project src/RocketWiki.Api
```

Health endpoints and `/graphql` respond, but data queries need a real database
and migrate-on-startup fails loudly without one (by design — set
`Database:MigrateOnStartup=false` to skip it), so this is mostly for
pipeline/middleware work. The integration tier is the honest way to exercise
the API container-free: it runs the real HTTP pipeline against EF Core on
SQLite. For the SPA alone, `VITE_FAKE_REALTIME=true npm run dev` swaps the
SignalR transports for in-memory fakes. See
[DEVELOPING.md](DEVELOPING.md#backend-standalone-no-docker).

## Project layout

```
RocketWiki/
├── design.md, data-model.md      source of truth — read these first
├── RocketWiki.sln
├── src/
│   ├── RocketWiki.AppHost/        Aspire topology (SQL Server, MinIO, Keycloak,
│   │                              draw.io, the API and web images, the AI
│   │                              connection strings)
│   ├── RocketWiki.ServiceDefaults/ health checks, OpenTelemetry, resilience
│   ├── RocketWiki.Api/            ASP.NET Core + Hot Chocolate GraphQL, JWT
│   │                              bearer auth, audit-declaration attributes,
│   │                              EF Core + file storage DI wiring,
│   │                              request-scoped AuditContext + JIT user
│   │                              provisioning + IAuditSink (design.md §7/§11)
│   ├── RocketWiki.Storage/        IFileStorage: FileSystem + S3 providers
│   ├── RocketWiki.Core/           domain model, rule engine, domain events
│   ├── RocketWiki.Data/           EF Core, migrations, SQL Server specifics
│   ├── RocketWiki.Sync/           low→high sync bundle export/import CLI
│   │                              (design.md §12)
│   └── RocketWiki.Importer/       Confluence space import pipeline (a
│                                  console tool — not consumed by the API)
├── tests/
│   ├── RocketWiki.Api.Tests/      schema-drift + audit-coverage checks, plus
│   │                              a SQLite-backed integration tier (§14)
│   ├── RocketWiki.Storage.Tests/  filesystem provider + DI wiring
│   ├── RocketWiki.Core.Tests/     rule engine, domain events (unit)
│   ├── RocketWiki.Data.Tests/     EF model against SQLite (integration)
│   ├── RocketWiki.Sync.Tests/     bundle export/import CLI
│   ├── RocketWiki.Importer.Tests/ Confluence import pipeline
│   └── RocketWiki.SqlServer.Tests/ Testcontainers tier against real SQL
│                                  Server (skips without Docker; CI's
│                                  `sqlserver` job is its first-class home)
└── web/                           Vite + React + TypeScript SPA
```

All fifteen .NET projects above are in `RocketWiki.sln`; `RocketWiki.Api`
references `RocketWiki.Core`, `RocketWiki.Data`, and `RocketWiki.Storage`.
The application service layer
(`RocketWiki.Core/Services/` — `IPageService`, `IPageReadService`,
`ICommentService`, `ILabelService`, `IAttachmentService`,
`IAttachmentReadService`) is now fully consumed: every GraphQL resolver and
mutation, and the two plain-HTTP attachment routes, route through it — never
a raw EF query or an entity's own navigation property, for exactly the
object-level-authorization reasons design.md §6.7/§8 calls for.

## Current status

Being explicit about what has and hasn't been checked, rather than letting
"it builds" stand in for "it works":

**A proposal, written but not built.** One document describes work nobody has
committed to. It is listed here because an unlinked plan is indistinguishable
from a plan nobody wrote, and it records choices that get expensive later:
[issue tracking and service desk](docs/PLATFORM-PLAN.md), which also carries the
design for forms on wiki pages as a ticket-creation front end. (Two earlier
proposals are gone. Restricted-page placeholders were retired rather than
built: the overhaul below makes disclosure always-on, and design.md §6.7
records why the opt-in toggle it proposed was rejected. Page entries — a
per-page record store with forms on top — were built and then removed: only the
form idea survived, re-homed in the platform plan, and the separate store did
not. The last tree carrying it is tagged `full-feature`.)

**In progress — not yet verified end-to-end:**
- **The protective-marking and access-model overhaul** (design.md §6.4, §6.7,
  §21, and the new §21.15). Four changes land together: **additional
  selectors** (instance-configured categories such as `FRUIT` with values
  `APPLE`/`BANANA`, conferred by a per-space access grant —
  `UK SECRET APPLE NORTH AUS/NZ EYES ONLY`); **access and
  role are separate grant kinds** (an access grant says who may see a space's
  pages and carries selector values; a role grant says who may edit or
  administer and confers no visibility — "viewer" is gone, and a space admin
  without an access grant manages a space whose every page shows as
  protected); **denial is disclosed on the page view, the tree and in-page
  links** as a `(protected)` placeholder carrying the marking label and every
  failing reason, while search, Ask, RQL, listings and MCP keep omitting, and
  a space you hold no access grant in withholds even the marking; and two
  spec changes — the caveat vocabulary becomes the fixed `AUS CAN NZ UK US`
  set rendered without brackets, and the prefix becomes a UK on/off toggle.
  **This supersedes four claims in the "Protective markings" bullet below**
  as written when it was verified: absent-not-forbidden now holds only on the
  omitting surfaces, the caveat vocabulary is fixed rather than the
  registry's, the classification no longer enforces at all (next bullet), and
  the absent-clearance floor — which this overhaul first moved to
  OFFICIAL-SENSITIVE — no longer exists. Two migrations
  (`AddMarkingSelectorsAndFixedCaveat`, `SplitSpaceGrantsIntoAccessAndRole`)
  convert existing rows behaviour-preservingly. **Done**: the overhaul is
  implemented and green on every test tier — the unit and SQLite integration
  tiers (Core, Data, API, Importer, Sync, Storage) and, on 2026-09-03, the
  real-SQL-Server tier run locally against SQL Server 2025 with Full-Text
  Search (46/46), whose two new migration tests seed the previous schema with
  viewer grants, `GB` caveats and a non-UK prefix and assert the conversion —
  the migration rehearsal in miniature. The suites include the marking truth
  table in Core (its current shape is in the next bullet), the persona × page
  truth table through the real API mutations, the placeholder introspection
  allowlist, the omitting-surface sweep, the telemetry-hygiene sweeps over
  the disclosing surfaces and the SPA's tests; the mutation drills behind
  each gate went red and were restored, four of them repeated by hand. **Also
  done, on 2026-09-03, against the live stack** (`aspire run`, Docker
  Desktop, a fresh Keycloak volume so the realm's then-new `fruit` mapper
  imported, and the existing SQL volume so both migrations ran on real dev
  rows — they did, converting the TEST space's admin-everyone grant into a
  role grant plus a mirror access grant): PKCE tokens for five dev users
  carried exactly the realm's claims; `me` reported the OFFICIAL-SENSITIVE
  floor for the claim-less user and an explicit OFFICIAL honoured;
  `selectorCategories` came back from the AppHost environment; an access
  grant carrying `FRUIT/APPLE` plus a page marked `UK OFFICIAL APPLE` gave the
  user granted but not eligible a `(protected)` placeholder with that label
  and the single failing eligibility gate, the eligible-and-granted users the
  page, the legacy `page(id)` null, the tree a `ProtectedTreeNode`, and search
  nothing; a role-only administrator of a new space saw it listed with
  `viewerHasAccess: false`, an empty tree, and a denial carrying only the
  space gate with the marking withheld. **That walk-through predates the next
  bullet**: the floor, the eligibility gate and the `fruit` mapper it
  exercised no longer exist, so two of its observations — the `me` floor and
  the "granted but not eligible" placeholder — can no longer be reproduced;
  the rest (grants, the role-only admin, the space-gate denial, search
  omitting) are unchanged in design and re-pinned by the current tests, but
  have not been re-walked live. **Not yet done**: a low→high bundle
  round-trip of a selector-bearing page between two running instances (the
  bundle path itself is pinned by the sync tests), and the browser SPA
  against the live API (the walk-through drove GraphQL directly; the SPA's
  tests run against a mocked transport).
- **The clearance and selector-eligibility gates are gone** (2026-09-04;
  design.md §21.2, §21.12, §21.15). This deployment's Keycloak carries no
  per-user clearance attribute and no per-category selector attribute, so the
  two gates that read them — C (clearance ≥ level) and E (a `yes` claim per
  category) — were removed rather than defaulted: a gate on a claim nobody
  emits is either an outage or a fake control. What is left is
  `canView = S ∧ G ∧ N ∧ R`, with a marking-availability check ahead of G:
  space access, every selector granted by a matched access grant, the
  eyes-only caveat against nationality, and the restriction chain. The
  **classification level is presentational**, exactly like the UK prefix: no
  gate reads it, no denial reason names it, any editor may set any level, and
  its ordering survives for the picker and the aggregate label. A page whose
  marking row is *missing* used to deny by being TOP SECRET and now denies by
  an explicit unavailable flag (`marking:unavailable`, wire gate
  `MARKING_UNAVAILABLE`), because a TOP SECRET row that gates nothing would
  have failed *open*. The profile page shows group memberships instead of a
  clearance and an eligibility table; `me` keeps `nationality` only; Keycloak
  needs `groups`, `nationality` and `roles` mappers and nothing else; a
  selector category is a name, a description and values, with no `ClaimName`.
  **Done**: the removal is implemented and green on every test tier,
  including the real-SQL-Server Testcontainers tier — the Core truth table is
  now 16 rows over S/G/N/R plus a sweep that the level changes no row, and
  the mutation drills (the availability flag, the grant check, the
  unknown-category branch, the caveat, the self-lockout rule, the principal's
  claim allowlist, the profile field list) each went red and were restored.
  **Sync follows the same rule** (design.md §21.10): a page arriving with no
  declared marking lands in a recorded *unavailable* state — `IsUnavailable`,
  a bit on both marking tables added by `AddMarkingUnavailableFlag`, set by
  the importer and cleared by any write that states a real marking — which
  reads back as the unavailable sentinel, so the page is readable by nobody
  and shows as the protected placeholder with the missing-marking reason until
  the origin sends a declared marking; an unavailable marking re-exports as no
  marking, never as a bare TOP SECRET. It used to land as a TOP SECRET row,
  which was a control only while the level gated; the refusal now comes from
  the recorded state, not from how high TOP SECRET is. Rows an older importer
  wrote that way are not backfilled (indistinguishable from a real prefix-less
  TOP SECRET); the migration's comment carries the review query. **Not yet
  done**: nothing
  about this change has run against the live stack (the dev realm's
  `clearance` and `fruit` mappers were removed in the same step and are
  unverified until the next `aspire run`), and the bundle round-trip above is
  still outstanding.

**Verified:**
- `dotnet build RocketWiki.sln` / `dotnet test RocketWiki.sln`, run solution-wide,
  reflect whatever every agent's projects look like *right now* — with several
  agents working in parallel, a transient break in one project (most recently,
  `RocketWiki.Importer`) is expected and not a signal the rest is broken. What's
  independently verified, per project this file's author owns: `RocketWiki.Api`
  / `RocketWiki.Api.Tests` and `RocketWiki.Storage` /
  `RocketWiki.Storage.Tests` build clean and their suites pass. No counts are
  written here on purpose: run each project's own `dotnet test <path>.csproj`
  rather than trust a number in a README — numbers drift, and a solution-wide
  run can fail for a reason that has nothing to do with the project you
  actually care about.
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
  **Gap closed this round**: denied *reads* are now audited with their
  reason. `IPageReadService` and `IAttachmentReadService` return the internal
  not-found-vs-denied result §6.7 specifies (`ReadResult<T>` /
  `AttachmentDownloadResult.Denied`), carrying the failing restriction the
  rule engine already computed (`restriction:{pageId}:{ruleId}`, or
  `no-space-role`); resolvers and the attachment route record it through
  `IAuditSink` (`ReadDenialAudit`) and then collapse it to the byte-identical
  null/empty/404 a genuine not-found gets — proven equal at the HTTP level,
  not just by parsed shape (`DeniedReadAuditTests`). A genuinely-missing page
  still audits nothing (no access decision was made; recording it as Denied
  would flood the probing signal with 404 noise), anonymous requests are
  unchanged, and tree *pruning* is deliberately not a denial — only a refused
  browse is. One sink-level rule made this safe: `DbAuditSink` suppresses a
  Success for a subject already recorded Denied in the same request, so a
  denied browse collapsed to an empty list never also claims success —
  confirmed to be load-bearing by disabling it and watching the test fail.
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
- **OpenTelemetry now covers the whole backend, and design.md §15's "telemetry
  is not audit" rule is enforced by a test rather than by intent.** Beyond
  Aspire's stock ASP.NET Core/HttpClient/runtime baseline, the GraphQL
  pipeline, SignalR hub invocations and connections, EF Core's own metrics,
  and the .NET 10 authorization/authentication metrics are all subscribed;
  the rule engine, domain-event and audit pipelines, sync outbox and bundle
  export/import, both `IFileStorage` providers, JIT provisioning, presence
  and notification fan-out, and the importer's pipeline passes each declare
  their own `ActivitySource`/`Meter` (see design.md §15 for the full table
  and naming scheme). What's verified by tests: that the key instruments
  actually emit with the tags claimed (`MetricCollector`/`ActivityListener`
  unit tests across Core, Data, Storage and Importer); that a hub invocation
  and a GraphQL request really do emit on the exact third-party source names
  ServiceDefaults hard-codes; that counters increment only after a commit,
  proven by forcing a failed `SaveChanges` and asserting nothing was
  counted; and — the §15 one — that no sentinel nationality value, page
  title, page content, search-shaped literal, or attachment byte reaches any
  span name, tag, event, baggage entry, or metric tag across *every*
  ActivitySource in the process. That last test was confirmed to fail when
  the rule is deliberately broken, three separate ways: enabling
  `RequestDetails.All` (the query document lands on a span), restoring
  `MaxErrorEvents` (Hot Chocolate's `graphql.error.message` quotes the
  client's own query text straight back), and tagging a metric with a rule
  expression (the nationality values being matched). All three were caught
  with a readable failure naming the exact tag, then reverted — so the two
  §15-driven deviations from the library's defaults are demonstrably
  load-bearing, not cargo-culted.
  Load-bearing finding along the way: `Aspire.Microsoft.EntityFrameworkCore.SqlServer`
  13.5.1's `AddSqlServerDbContext` registers **only** `AddSqlClientInstrumentation()`
  plus a DbContext health check — no EF Core instrumentation and **no metrics
  at all** (confirmed by decompiling the package, same practice as the
  Keycloak realm work). So SQL query tracing was already covered and must not
  be registered a second time, while EF Core's meter was a genuine gap.
  Second finding, about the tests rather than the code: an `ActivitySource`
  and a `Meter` are process-wide, so a listener in one test class captures
  what a class running in parallel emits. The telemetry test classes are in a
  non-parallelizable xUnit collection for that reason; without it the
  assertions would have had to be weakened to "contains something of roughly
  this shape", which would pass even if the instrumentation emitted nothing.
- **Browser telemetry is wired and unit-tested** (a frontend agent's work,
  reported rather than independently reverified here): `web/src/telemetry`
  adds OpenTelemetry to the SPA behind a configuration gate — with no
  `VITE_OTEL_EXPORTER_OTLP_ENDPOINT` set, the tracing SDK is never even
  downloaded; Vite splits it into its own 75 kB chunk that an unconfigured
  build never requests. 73 tests cover the gate (off when unset, and off for
  a malformed endpoint), the query/fragment redaction that keeps search text
  and heading-anchor slugs out of spans (design.md §15), the `traceparent`
  allowlist checked against the SDK's own matcher rather than our
  assumptions about it, and the GraphQL operation-name enrichment —
  including an assertion that no variable value, document text, or response
  body reaches a span.
- **The MCP server is real and adversarially tested** (design.md §8,
  milestone 8): `/mcp` runs in the API process over the official C# SDK
  (stateless streamable HTTP), so every tool call is an individually
  authenticated request through the same JIT-provisioning and audit pipeline
  as GraphQL. The four read-only v1 tools call the identical services the
  resolvers call — never raw EF — and a restricted page or space is
  *byte-identical* on the wire to a nonexistent one (proven by
  serialized-result equality), while the restricted case — and only it —
  writes a Denied `mcp`-channel audit row with its failing restriction
  through the same `ReadDenialAudit` path GraphQL uses. Anonymous calls die
  at the pipeline with the MCP spec's resource-metadata discovery challenge;
  every successful call writes an `mcp`-channel audit row carrying the
  client's self-reported name; the audit-declaration guard now covers tools
  (build-time sweep plus a runtime refusal for undeclared tools, both proven
  to fire). Unverified, same as everything Keycloak: no real OAuth flow has
  run — discovery shape and token-validation wiring are tested, a live
  client obtaining a Keycloak token is not; and RFC 8707 resource-audience
  alignment is an open realm-config question.
- **The k3s deployment package (milestone 9): the images run daily, the
  chart does not.** `Dockerfile.api` and `Dockerfile.web` are what the Aspire
  AppHost builds and runs on every start (since 2026-10-07 — see "Verified on
  real containers" below), so the publish, the EF migrations-bundle step, the
  Vite build, nginx's envsubst config and its proxying are exercised
  continuously. What that does *not* cover: the Helm chart in
  `deploy/helm/rocketwiki/` (`helm lint` passes and `helm template` renders
  valid YAML including the guard rails — api.replicaCount hard-locked to 1,
  telemetry export fails closed, secrets referenced never templated — and
  that is the whole claim; nothing has been applied to a cluster), the
  migrations bundle's *execution* (the AppHost overrides the image back to
  migrate-on-startup; the bundle is built every time and has never been run),
  and the images' Production-mode defaults as such (the AppHost runs them as
  Development). `deploy/README.md` has the build/offline-install/restore-drill
  procedures and its own, longer "what has never been verified" list.
- `RocketWiki.Core.Tests` and `RocketWiki.Data.Tests` have passed as part of
  a solution-wide run in the past — not
  independently reverified by this file's author this round (see the
  solution-wide build/test caveat above); the EF model runs against SQLite in
  that suite too, independently of the API-level integration tier above.

**Verified by tests and CI** (the standing caveat — design.md §16 —
applies: test-proven, never yet run against live infrastructure; each
bullet keeps its own sharper caveat where one exists):
- **Protective markings** (design.md §21) — *as verified on the day, and kept
  as that record; the two bullets under "In progress" above say what has
  changed since, chiefly that the classification no longer enforces and there
  is no clearance floor (2026-09-04).* Every page carries a UK
  Government classification (`OFFICIAL` < `OFFICIAL_SENSITIVE` < `SECRET` <
  `TOP_SECRET`) plus an optional *eyes-only* set of countries it is
  releasable to, written with a national prefix — `UK SECRET`,
  `UK OFFICIAL [GB EYES ONLY]` — which defaults to `UK`, is clearable per
  page, and is **purely presentational**: it is not read by the clearance
  gate, never appears in a denial reason, and changes no access decision.
  **The classification itself enforces.**
  Effective view access is `canView AND clearanceAllows(marking,
  principal)`, and a classification can only ever *subtract*: no space
  grant, space-admin or instance-admin role reads around it, exactly as
  none of them reads around a page restriction. The check sits inside the
  one `EffectivePermissionCalculator.Compute` every read path funnels
  through, so pages, the tree, search snippets, Ask citations, MCP,
  attachments, comments and notifications inherit it structurally rather
  than by remembering. A page you lack clearance for is **absent, not
  forbidden** — byte-identical to one that doesn't exist, with the real
  reason in the audit log. Clearance is a principal attribute from the
  OIDC token; **absent or unrecognised clearance grants OFFICIAL and
  nothing above** — the deliberate middle between "see everything" and an
  outage that gets the control switched off. The eyes-only vocabulary is
  the registered `nationality` attribute's allowed values, never an
  invented ISO list, so both sides of the comparison speak the same
  language. Markings cross the sync boundary with content and a page
  cannot land on the high side unmarked. Downgrades carry their own audit
  action so every widening is one query away.

  Results are marked, and so is what contains them (design.md §21.13).
  Search hits, MCP items and Ask citations each carry their own page's
  marking, and the containing result carries the **aggregate**: the highest
  classification among everything that fed it. For an Ask answer that means
  everything that entered the model context, cited or not — a
  retrieved-but-uncited `UK SECRET` page still makes the answer `UK SECRET`,
  because a marking a model could defeat by declining to cite would not be a
  marking. Caveats are a truthful conjunction rather than a merge: two
  sources released to different countries render
  `UK SECRET [GB EYES ONLY] [US EYES ONLY]`, never their union (a widening)
  and never their intersection (which would be empty, and an empty caveat
  reads as *no* caveat — the most restrictive inputs producing the least
  restrictive output). An aggregate is a **display label**, not a marking:
  computed after enforcement from pages you were already permitted to see,
  never stored, and gating nothing — the type lives in the API layer, which
  the rule engine and every read service cannot reference.

  > ⚠️ **The `AddPageMarkings` migration backfills every pre-existing page
  > to OFFICIAL — the lowest level.** That is the pragmatic call, not the
  > safe one: nobody has reviewed that content and it now wears a marking
  > saying it is fine. Existing content must be reviewed and re-marked;
  > find what is still untouched with `SELECT PageId FROM PageMarkings
  > WHERE SetByUserId IS NULL`. Backfilling to TOP SECRET would have
  > locked the wiki out of itself.
- **Page properties** (design.md §20): pages carry admin-defined key/value
  metadata — `Owner`, `Review Date`, `Status` — edited on a dedicated
  properties screen, never inside the page body. Keys come from an
  instance-level registry only instance admins can change; page editors
  pick a key and supply plain text. Reading needs `canView`, writing needs
  `canEdit`, replicas are read-only, and deleting a registry key that pages
  still use is refused with the count. Being structured data rather than
  content is the point: properties never enter the Markdown round-trip, the
  co-editing document, or the Confluence importer — which also means they
  are **not searchable** and are **absent from a Markdown export**. Sync
  carries the key by name so a replica can materialise a key it has never
  seen.
- **SQL Server blob storage** (`FileStorage:Provider=SqlServer`), a third
  `IFileStorage` alongside filesystem and S3 — deliberately **not** the
  recommended default (transaction-log churn, backup size, buffer-pool
  pressure, no CDN path), but the one option where a single database backup
  covers content *and* attachments at one point in time: useful for
  air-gapped or single-container installs, dev/test without a MinIO, and
  one-step restore drills. Reads stream via a sequential-access reader and
  uploads append in chunks, both pinned by allocation tripwires; the table
  is created on first use, outside the EF migration chain. Behaviour is
  verified against a real engine in the Testcontainers tier.
- **Backend consistency round (from the consistency reviews)**: the three
  binary routes share one error-status map, 413 shape and upload-cap guard
  (they had drifted into three partial maps); the options families now
  validate at startup, so a non-positive size cap fails the host at boot
  instead of every upload at runtime; telemetry naming is enforced by a
  reflection guard rather than convention; and log records emitted during
  `GET /avatar/{hash}` no longer reach the exporter — the logging
  counterpart of the tracing exclusion that already existed, closing an
  email-hash leak via the `RequestPath` scope.
- **Consolidation round (from the consistency reviews)**: one
  `PermissionContextLoader` replaces fourteen hand-rolled copies of the
  grants + ancestor-chain + restrictions load, and with it the audited
  denial reason became deterministic everywhere (§6.7) rather than
  following database enumeration order — pinned by a regression test that
  fails against the old code. Frontend: route folder holds routes only
  (five dialogs moved to their owning features), load failures speak one
  vocabulary distinct from empty states, and an actionable upload error is
  an inline Alert rather than a snackbar that can vanish unread.
- **UX polish round (from the consistency reviews)**: author-supplied alt
  text for draw.io (optional `alt:` first line of the fence body, inert to
  sync and importer) and mermaid (`accTitle:`/`accDescr:` surface properly);
  Ctrl/Cmd+Enter submits comments with visible helper text; degraded-state
  copy is centralized (`web/src/feedback/unavailableCopy.ts`) with "replica"
  as the one user-facing word for synced spaces; blob caches and authed
  fetch are shared modules (fixing an emoji swap-flicker bug); read-only
  page views no longer download the Yjs/co-edit chunk (~126 kB deferred to
  edit-session start).
- **Hardening round (from the consistency reviews)**: attachment downloads
  carry nosniff/no-cache/ETag-304 semantics without weakening per-read
  auditing (a 304 still runs `canView` and writes its audit row — tested);
  storage-key validation is shared by both `IFileStorage` providers (S3
  previously passed keys unvalidated); the "no presigned URLs" invariant and
  the audit-declaration rule for REST routes both gained build-time tripwire
  tests; denial audit rows use one `{"reason": ...}` shape everywhere.
- **The SPA is fully wired to the reconciled schema**: server-computed
  permissions shape every affordance (edit/move/delete/comment/permissions),
  page restrictions and the effectivePermission inspector are live, replica
  spaces are marked proactively, and watches/labels/archived-spaces/audit
  totals read server truth. No `NOTE (schema reconciliation)` markers
  remain in `web/src`.
- **Permissions shape the UI** (design.md §6.6): `Page` exposes
  `canEdit`/`canComment`/`canManageAccess` for the current caller (batched,
  replica-aware), a manage-gated `restrictions` listing, tree lock markers,
  and an `effectivePermission` inspector with per-rule pass/fail — all
  fail-closed, all audited (`permission.inspect`). The SPA's already-built
  permission components wire up in the un-stub round.
- **Collaborative editor (SPA, design.md §8)** — the TipTap editor now
  co-edits over the existing hub connection: a hand-rolled SignalR Yjs
  provider (no y-websocket) joins the relay session, seeds or replays per
  the server's role assignment, batches keystrokes into merged updates and
  throttles carets on the real awareness protocol, and handles log-cap
  save-and-reseed, seeder loss, eviction to read-only, and reconnect
  (rejoin-and-replay; the one divergent-lineage corner that can duplicate
  text is documented in the provider, not hidden). A refused or unreachable
  join falls back to the unchanged solo editor — co-editing is progressive
  enhancement. Session saves submit against the session base revision and
  toast the server-resolved contributors. Building it surfaced a real
  round-trip bug (StarterKit
  v3's trailing-node chrome serialized as a phantom blank paragraph — fixed
  at the serializer, corpus still byte-identical). Two real editors syncing
  over a scripted relay are under test; per the standing caveat, no browser
  has yet driven the live hub.
- **Ask the wiki (backend, design.md §9.5)** — `askWiki(question)` answers
  from wiki content with per-section citations, retrieving only pages *you*
  can view (the same permission-filtered search and canView-gated reads as
  everything else) before any text reaches the model — proven by a test
  that records every message sent to a fake model and asserts a restricted
  page's sentinel never appears for a non-cleared asker. Honest scope: your
  question and the retrieved viewable content are sent to the
  operator-configured, in-network LLM endpoint; unconfigured instances
  answer `NOT_CONFIGURED`, and every ask is audited (`assistant.ask`) with
  the question and the pages involved. The chat UI lives at `/ask` (an app-bar
  entry beside search, plus a "Can't find it?" hand-off from search results):
  a session-local transcript, `[Sn]` markers rendered as superscript
  deep-links into the cited page sections, the model's output rendered
  strictly as text, and every affordance collapsing for the session on an
  unconfigured instance — the attempt is the probe, since no status field
  exists.
- **Native vector search (design.md §9.3)** — `PageEmbedding` now uses SQL
  Server 2025's `vector(1536)` column with in-engine `VECTOR_DISTANCE`
  scoring (CI-verified by the Testcontainers tier, including §6.7 on that
  path). The DiskANN index is deliberately deferred with engine-verified
  reasons (`VECTOR_DISTANCE` is index-blind by documentation; boxed 2025's
  index format is preview-gated and makes the table read-only) — both
  limitations are tripwire-tested so their lifting is detected, not hoped
  for.
- **Real-time co-editing (backend, design.md §8)** — Yjs relay sessions
  over the SignalR hub: canEdit-gated and session-audited (join denials
  recorded with their failing restriction), seeder/reseed protocol with log
  caps, rule-change eviction closing the relay, and multi-author revision
  attribution (`PageRevisionContributor`, resolved only from server-side
  session state — client-supplied contributor lists don't exist in the
  API). The collaborative editor UI (TipTap + Yjs, live carets) is the
  in-flight phase 2; the SignalR transport's default 32 KB receive limit
  was raised to fit real Yjs seeds — found by test, not in production.
- **Profile pictures + Gravatar endpoint (backend, design.md §19)** —
  user-uploaded avatars (PNG/JPEG/WebP in, server-normalized canonical
  512×512 PNG stored — originals and their EXIF never persisted), self-only
  set/clear, audited; `UserRef.hasAvatar`/`me.hasAvatar` for rendering.
  Optionally the wiki serves the Gravatar/Libravatar protocol at
  `GET /avatar/{hash}` for other in-network tools — **honest caveat: this
  endpoint is unauthenticated by design** (that's the protocol), so it is
  off by default (`Avatars:GravatarEndpointEnabled`) and enabling it means
  anyone on the network can fetch avatars and probe which email hashes have
  one; the network boundary is the only wall. Example consumer: point
  GitLab at the wiki with `gravatar_enabled: true` and
  `gravatar_url: "https://wiki.example.com/avatar/%{hash}?s=%{size}&d=404"`.
- **Custom emojis (backend, design.md §19)** — instance admins manage a
  `:name:` registry (`POST/DELETE /emojis/{name}`, audited); any
  authenticated user gets `GET /emojis/{name}` (ETag/304) and the
  `customEmojis` GraphQL list. Uploads are normalized via
  SixLabors.ImageSharp 3.1.12 (**Split License** — Apache-2.0 for
  open-source/small-org use, commercial otherwise; 4.x additionally needs a
  build-time license key, so upgrading is a version bump plus that key):
  decode-limited, squared to 32–256 px, re-encoded with metadata stripped;
  animated GIF supported (64-frame cap).
- **Profile pictures & custom emojis (SPA)** — upload an avatar in Settings
  (server-normalized to a square 512 PNG, metadata stripped); faces appear
  beside comments, presence, and bylines, with initials as the fallback.
  Instance admins curate a `:name:` emoji registry under Admin → Custom
  emojis; type `:` in the editor to autocomplete, and unknown names simply
  stay text — including in content synced from another instance. Emoji
  rendering is a read-mode decoration over literal text, so the Markdown
  round-trip is untouched by construction (corpus vectors prove it).
- **GitLab integration (backend, design.md §18)**: `gitlabIssue`/
  `gitlabIssues`/`gitlabFile` queries proxying GitLab REST v4 under the
  *calling user's own* encrypted PAT (no service account, ever — the
  read-around §6 forbids; adversarial cross-user credential isolation is
  pinned by test), fail-closed on `GitLab:BaseUrl`, live-never-cached with
  typed degradation, audited as `gitlab.fetch`, token write-only by
  construction. Along the way it surfaced and fixed a real audit bug:
  `DbAuditSink`'s per-request dedup never actually spanned resolver scopes
  (Hot Chocolate resolvers don't share the middleware's DI scope — the same
  boundary Program.cs documents); it now dedups via `HttpContext.Items`,
  proven by an alias test. Unverified: no live GitLab has ever answered
  these clients — REST shapes come from docs + a faked wire.
- **GitLab integration (SPA)** — link issues with live open/closed status,
  embed repository files, and list issues by filter, all fetched through
  the wiki API with *your own* GitLab token (set it under Settings;
  `read_api` scope is enough). References are host-free and stay plain
  Markdown, so pages sync and export unchanged even where GitLab is
  unreachable. Every GitLab affordance (toolbar menu, chips, settings
  section) vanishes entirely when `GitLab:BaseUrl` is unconfigured.
- **Accessibility**: WCAG 2.2 AA target, enforced by two automated layers —
  axe on every component test run, and a real-Chromium axe pass over every
  screen in both themes in CI. Coverage, exclusions, and the manual-audit
  scope: [docs/ACCESSIBILITY.md](docs/ACCESSIBILITY.md). Remediation
  surfaced a latent dark-mode bug (the app never stamped `data-theme`, so
  dark mode showed light-variable code blocks) and replaced luck with math
  for presence-color label contrast.
- **Tables** support GFM column alignment, merged cells (colspan/rowspan
  via MultiMarkdown syntax), and `<br>` line breaks inside cells — all
  round-tripping byte-identically through the editor. The Confluence
  importer preserves them too — merged cells, alignment, and cell line
  breaks convert at full fidelity (only header-boundary-crossing rowspans
  still degrade, split with a conversion-report note; captions, previously
  dropped silently, now flatten with a note — a fixed bug).
- **Edit conflicts show a Markdown diff** of the other author's revision
  against your draft right in the conflict dialog, so you can keep yours,
  take theirs (your text goes to the clipboard), or keep editing.
- **Diagrams**: ` ```mermaid ` blocks render inline (live preview while
  editing), and draw.io diagrams embed as base64 editable SVG with in-place
  editing via a configured diagrams.net instance (`VITE_DRAWIO_URL`,
  fail-closed). Both are plain fenced Markdown — round-trip, sync, and
  import untouched. The diagrams.net postMessage handshake has never run
  against a live instance (standing container caveat).
- **The frontend** now generates its typed client from the exported
  `schema.graphql` and defaults to the real SignalR transports against
  `/hubs/notifications` (fakes only behind `VITE_FAKE_REALTIME`) — but no
  browser has ever connected to the hub or executed a query against a live
  API; everything is verified by vitest against a mocked exchange and a
  mocked hub connection builder. The generated client is deliberately
  untracked; CI runs `npm run codegen` from the committed `schema.graphql`
  before building.

**CI-verified against real SQL Server (never yet on a developer machine):**
- The checked-in migrations apply from zero to a real,
  FTS-enabled SQL Server 2025 container — including the FULLTEXT
  catalog/index DDL, filtered indexes, CHECK constraints, and the
  AuditEvents IDENTITY — and `Database:MigrateOnStartup`'s happy path boots
  the real API host, migrates, and answers `/graphql`. Keyword search's
  CONTAINSTABLE branch is exercised for real (stemming-only matches, FTS
  ranking, permission filtering on that branch), as are outbox sequence
  gap-freedom, audit-transaction rollback, and declared-length enforcement.
  Building this tier surfaced two real bugs before any engine ever ran the
  code: the FULLTEXT DDL could never have applied inside EF's migration
  transaction (now `suppressTransaction: true`), and raw user queries were
  CONTAINS-grammar syntax errors (now a quoted `FORMSOF(INFLECTIONAL, …)`
  builder, unit-tested). All of this runs only in CI's `sqlserver` job
  (`tests/RocketWiki.SqlServer.Tests`); on machines without Docker the tier
  skips visibly and these claims are only as fresh as the last green CI
  run.

**Not built:**
- **No marking is set at page-creation time** (design.md §21). A new page
  inherits its parent's marking, or OFFICIAL at the root — so a *root*
  page created with classified content sits at OFFICIAL until someone
  changes it. Closing this means putting the marking on the create form
  and in `CreatePageRequest`; until then it is a procedural control, not a
  technical one. Also not built: a "which pages are marked X" report,
  subtree re-marking, declassification schedules, and any caveat kind
  beyond eyes-only.
- **Cross-page property reporting.** There is no "every page in this space
  with `Status: Draft`" query and no property facet in search — the data
  model supports one without a migration, but the query surface does not
  exist (design.md §20.5). Related smaller gaps in the same feature:
  registry keys can be created and deleted but not renamed or reordered,
  and a local property change dispatches no watcher notification while a
  synced one does. (The *baseline*-bundle gap that used to be listed here
  is closed — see the sync note below.)
- **Baseline sync bundles are complete now, and were not.** Until the
  engineering-review round, a baseline carried pages (and the since-retired
  page entries) and nothing else: a space flagged for export *after* it
  already had content delivered that content stripped of its restrictions,
  comments, attachments, labels and properties. Restrictions were the sharp
  edge —
  a page restricted on low landed on the high side with no restriction row
  at all, readable by every viewer of the replica, which is fail-*open* and
  the one direction §12 takes nowhere else. All five now cross
  (design.md §12). Two deliberate asymmetries remain, both stated there:
  comment tombstones cross (a live reply's `ParentCommentId` needs its
  parent row) while soft-deleted attachments do not (nothing references
  them, and shipping deleted bytes across a one-way boundary is the wrong
  default).
- **The design.md §10 storage janitor does not exist.** §10 promises "a
  nightly janitor deletes storage objects with no matching row" — that job
  has never been written, and this ledger previously didn't say so. What
  that means in practice: object storage grows monotonically. Every
  failed-commit upload path (attachments, avatars, emojis), every replaced
  avatar, and every best-effort emoji blob delete deliberately leaves an
  orphaned object *for* the janitor — the code sites carry a greppable
  `JANITOR(§10)` comment tag — so those orphans currently accumulate
  forever. Building it is also not just "write the job": `IFileStorage`
  has no enumeration primitive (Save/OpenRead/Delete/Exists only), so a
  janitor that finds unreferenced objects requires an interface change
  across all three providers first (filesystem, S3, SQL Server).

**Verified on real containers (2026-08-28, extended 2026-10-07).** The
standing "nothing has ever run against real infrastructure" caveat is now
retired. `aspire run` was executed on Docker Desktop 4.87.0 (engine 29.7.2,
Linux containers) from *empty volumes*, and the following were observed rather
than reasoned about:

- **The API and the SPA run as their deployment images** (2026-10-07). The
  AppHost builds `Dockerfile.api` and `Dockerfile.web` and runs the results
  in place of the project and the Vite dev server: migrations applied from the
  API container against the SQL Server container; a PKCE sign-in by a dev user
  through the web image's nginx proxy, with the token minted by Keycloak under
  the browser's address and validated by an API that reaches Keycloak under the
  container network's (`KC_HOSTNAME` + backchannel-dynamic, `AppHost.cs`);
  the security headers on the served SPA; and Nitro served from the embedded
  copy with no CDN request. The Vite build inside Docker — the one stage of
  `Dockerfile.web` that had never run — now runs on every start.

- **The whole Aspire topology boots** — SQL Server, Keycloak, MinIO and
  draw.io containers plus the API project, with `WithReference`, service
  discovery and connection-string injection doing what they were supposed to.
- **Migrations apply from zero against a real SQL Server 2025**
  (17.0.4075.5, Full-Text Search installed): all 10 migrations, 28 tables, the
  full-text index present, and `PageEmbeddings.Embedding` bound to the native
  `vector` type. `CONTAINSTABLE` returns real hits against real page content.
- **The Keycloak dev realm imports and issues usable tokens.** All six dev
  users complete an Authorization Code + PKCE flow; the decoded access tokens
  carry `sub`, `preferred_username`, `email`, `name`, `aud: rocketwiki-api`,
  `groups` as bare names, multivalued `nationality` (including the dual
  national), `clearance` (a claim the realm has since stopped emitting — the
  gate that read it went on 2026-09-04), and `roles` — with `carol.noattr`
  carrying no attribute claims at all, which is §6.3's fail-closed case
  behaving.
- **The full authentication path**: token → JWT validation → `PrincipalBuilder`
  → JIT provisioning → GraphQL. An ABAC grant resolved from a real `groups`
  claim and let its holder create a page; the instance-admin gate accepted
  `frank.admin` and refused everyone else.
- **The S3 provider against live MinIO** — upload, download and the object
  present in the bucket under the expected `attachments/YYYY/MM/<id>` key.
- **OpenTelemetry leaves the process.** Traces, metrics and logs were captured
  off the wire at a real OTLP endpoint. Both previously-open questions are
  answered: ServiceDefaults' `RocketWiki.*` wildcard **does** pick the custom
  sources up against a live SDK (trace scope `RocketWiki.Storage`; metric
  scopes `RocketWiki.Api`, `RocketWiki.Core`, `RocketWiki.Storage`, carrying
  `rocketwiki.access.*`, `rocketwiki.audit.*`, `rocketwiki.domain_events.*`,
  `rocketwiki.identity.*`, `rocketwiki.storage.*`), and span volume is modest —
  roughly five GraphQL spans plus the ASP.NET span per request, with no
  per-field resolver spans.

That run, and the first real use of the SPA against it, found **twelve defects**
every prior form of review had missed. Five were in shipped application code:

- the dev realm issued tokens with **no `sub` claim**, so nobody could
  authenticate at all;
- **no caller was ever an instance admin** — ASP.NET renames the `roles` claim
  to `ClaimTypes.Role`, and only one lookup didn't know;
- **every S3 upload failed over plain HTTP**, because payload signing was
  unconditionally disabled, which the AWS SDK forbids without TLS — i.e.
  against exactly the in-network MinIO/Ceph deployments §9.4 describes;
- **sign-in never completed**: the OIDC state store was in-memory, and sign-in
  state has to survive the navigation to Keycloak and back, so the code
  exchange failed every time and the SPA sat on "Completing sign-in…";
- **the notifications hub never connected**, because StrictMode's double mount
  raced `start()` against `stop()` and `withAutomaticReconnect` only resumes a
  connection that succeeded once — realtime was dead for the whole session;
- and **every page view failed** with "Couldn't load this page": Hot Chocolate
  resolves sibling fields in parallel, and the DataLoaders behind them shared
  the request-scoped `DbContext`, so EF Core threw "a second operation was
  started on this context instance" on six fields at once.

Five more were AppHost wiring — a stock SQL Server image with neither full-text
search nor the `vector` type, unresolved AI connection strings that silently
held the API in a pending state forever, no `WaitFor` on the database (so
migrate-on-startup lost a race and crashed), a Keycloak reference that injected
service-discovery variables while the API read a connection string, and a MinIO
container nothing was configured to use. The last was a test that only passed
while the app was *mis*configured. All twelve are fixed, with regression tests
where the defect was in code.

Note what the last one required: it reproduces only against a real database,
because in-process SQLite answers a batch fast enough that the parallel
dispatches never overlap. Its regression test therefore lives in the §14
container tier, not the SQLite one.

A thirteenth came out of more probing of the same stack the next day:
**every audited list query crashed for an unauthenticated caller.** Anonymous
callers get an empty list by convention, and the audit sink refuses to write a
row with no acting user by design; nothing in between said that an anonymous
request has no row to write, so eleven root fields answered "Unexpected
Execution Error" instead of `[]`. The SQLite tier could have caught this one — a
test client with no claims header really is anonymous — but no test had ever
aimed one at an audited list.

**Still explicitly unverified:**
- **That the API *container's* telemetry reaches the dashboard.** The project
  resource's did (above); the container gets the same exporter configuration
  from `WithOtlpExporter()`, the dashboard's OTLP endpoint answers from inside
  the container network, and the exporter logs no errors — but receipt was not
  observed on the dashboard itself during the 2026-10-07 run, which was
  scripted and never opened it. Open the Traces page after a sign-in and look
  for `api`; if it is empty, the TLS trust of the dashboard's dev certificate
  inside the container is the first suspect.
- **That any browser span has ever been exported to a real OTLP endpoint.**
  The exporter's transport is mocked in the web tests; the only evidence the
  export leg does anything at all is an early draft that let it run for real
  and produced `ECONNREFUSED` against a dashboard that wasn't running. The
  AppHost now runs the SPA as the web image, but `VITE_OTEL_*` are build-time
  values it does not bake (the dashboard's OTLP endpoint and its CORS allow-list
  would both have to be known at image build), so nothing injects the endpoint
  automatically yet — `web/.env.example` documents the variables for standalone
  `vite dev`, including the easily-missed detail that the dashboard's OTLP/HTTP
  port is 18890, not the gRPC 18889.
- **Token renewal in the browser.** `npm run dev` has now been pointed at the
  live stack, and a real browser has completed the login redirect, loaded and
  created pages, and held an open SignalR hub connection — the three defects
  that turned up doing it are listed above. What remains unobserved is the
  *silent renew* path: no session has been left running long enough for an
  access token to expire and be refreshed through the `silent_redirect_uri`
  iframe, which is the one part of design.md §11's auth story a short manual
  session never reaches.
- **Milestone 5, the migration trial**, still needs a real Confluence export.
- **The k3s deployment.** `deploy/helm` is lint- and render-verified only; no
  chart has been installed into a cluster. See `deploy/README.md`.
