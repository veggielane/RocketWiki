# Developing RocketWiki locally

Honest before helpful: parts of this project are verified by tests and CI,
not by having been run. Where a path below has never actually been walked on
a developer machine, it says so. `design.md` is the constitution — read §14
(test strategy), §15 (deployment/config), and §16 (milestones + the standing
caveat) before trusting anything, including this file. Every configuration
key (backend and `VITE_*`), its default, and what unset means is catalogued
in [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0.x | everything backend |
| Node.js | 24.x | the SPA (`web/`) |
| Docker | any recent | the full Aspire stack **and** the SQL Server test tier — both optional; everything else works without it |
| Helm | 4.x | only if you touch `deploy/helm` (`helm lint --strict`) |

The first two are pinned in-repo rather than left to convention: `global.json`
accepts any 10.0 feature band and refuses anything else (a 9.x SDK fails with
a message naming the requirement instead of a confusing build error), and
`.nvmrc` is the single source of truth for the Node major — CI reads it via
`node-version-file` rather than repeating "24" per job.

## The 90-second loop (no Docker required)

```bash
# Backend: build + the whole SQLite-tier suite (~900 tests, well under a minute)
dotnet test RocketWiki.sln
# The RocketWiki.SqlServer.Tests project shows as SKIPPED without Docker —
# visible, deliberate, and green. CI runs it for real (see "Test tiers" below).

# Frontend
cd web
npm ci
npm run codegen     # REQUIRED: the typed GraphQL client is generated from
                    # ../schema.graphql and deliberately not committed.
                    # Nothing compiles without this step. CI does the same.
npm test
npm run lint
npm run build
```

## Running the app

### Full stack (Docker) — `aspire run`

```bash
dotnet run --project src/RocketWiki.AppHost
```

This starts SQL Server (with a data volume), Keycloak (dev realm
auto-imported: six users covering the rule engine's edge cases, all password
`RocketWiki!Dev1` — see `src/RocketWiki.AppHost/keycloak/README.md`), MinIO,
and a draw.io container for the diagram editor, then the API with connection
strings injected. The Aspire dashboard URL is printed at startup; it also
receives all OpenTelemetry (traces/metrics/logs) automatically.

The Vite app is **not** in the AppHost yet (a commented TODO in
`AppHost.cs`). Run it separately:

```bash
cd web && npm run dev        # http://localhost:5173
```

The Vite dev server proxies `/graphql`, `/hubs` (WebSocket), `/attachments`,
`/avatars`, `/avatar`, `/emojis`, and `/users` to the API — the API
deliberately has no CORS policy, so always go through the proxy. Under
Aspire the API's port is assigned dynamically: read it off the dashboard and
set `VITE_API_TARGET=http://localhost:<port>` before `npm run dev`. Point
`VITE_OIDC_AUTHORITY` at the Keycloak the dashboard shows (realm
`rocketwiki`). All frontend env vars are documented in `web/.env.example`.

Two things to know before the first run on a new machine:

- **Trust the ASP.NET dev certificate first** (`dotnet dev-certs https
  --trust`, then accept the Windows prompt). The Aspire CLI tries to do this
  for you and will sit on a modal dialog until someone clicks it, which looks
  exactly like a hung build if you started it from a script.
- **The first start builds the SQL Server image** from `docker/mssql-fts`
  (a few minutes; cached afterwards). Aspire's default `AddSqlServer` image
  cannot run this application at all — see that Dockerfile's header.

The Keycloak realm is imported only into a *fresh* data volume. After editing
`rocketwiki-realm.json`, `docker volume rm` the keycloak volume or the old
realm persists and your change appears to do nothing.

> **Retired caveat (design.md §16):** this section used to warn that
> `aspire run` had never actually been executed. It has, on 2026-08-28, from
> empty volumes: containers up, migrations applied to a real SQL Server 2025,
> every dev user logged in through PKCE, attachments round-tripped through
> MinIO, telemetry captured off the wire. See the README's status section for
> what that run verified — and for the eight defects it found, which is the
> honest argument for doing it sooner.

### Backend standalone (no Docker)

```bash
dotnet run --project src/RocketWiki.Api    # http://localhost:5079
```

Health endpoints (`/health`, `/alive`) and `/graphql` respond. Without a
reachable SQL Server, migrate-on-startup fails **loudly** (by design — set
`Database:MigrateOnStartup=false` to skip it), and data queries need a real
database, so this mode is mostly useful for pipeline/middleware work. The
integration tests are the honest way to exercise the API without
containers: they run the real HTTP pipeline against EF Core on SQLite
(`tests/RocketWiki.Api.Tests/Integration/RocketWikiApiFactory.cs`).

### Frontend without any backend

```bash
cd web && VITE_FAKE_REALTIME=true npm run dev
```

`VITE_FAKE_REALTIME=true` swaps the SignalR transports for in-memory fakes
(any other value means real — a typo can't silently disconnect you).
GraphQL calls will still fail without an API; component work is generally
better done through the vitest suites, which mock the urql exchange
(`web/src/test/mockUrqlClient.ts`).

## Test tiers (design.md §14)

1. **Unit** — Core/Data/Storage/Importer/Sync test projects, plus the web
   vitest suites. Fast, no I/O beyond temp files and in-memory SQLite.
2. **SQLite integration** — the bulk of the suite: the real ASP.NET Core +
   Hot Chocolate pipeline, real services, EF Core on SQLite, fake Keycloak
   auth handler. `dotnet test tests/RocketWiki.Api.Tests`.
3. **SQL Server (Testcontainers)** — `tests/RocketWiki.SqlServer.Tests`:
   the checked-in migrations applied from zero to a real FTS-enabled SQL
   Server 2025, CONTAINSTABLE search, migrate-on-startup, dialect-sensitive
   service behavior. **Docker presence is the switch**: no daemon → the
   whole project skips visibly; with Docker it runs locally too (first run
   builds the FTS image from `docker/mssql-fts/` — the same image `aspire run`
   uses, which is why that Dockerfile lives at the repo root rather than
   inside this test project).
   CI's `sqlserver` job is its first-class home and fails if the tier skips.

**Accessibility (two tiers).** The vitest suite runs axe (WCAG 2.2 AA,
minus three rules jsdom cannot answer — color-contrast, target-size,
link-in-text-block) over every page-level test and a whole-screen sweep;
it's part of the ordinary `npm test`. The browser tier renders every major
screen in both themes to standalone HTML
(`PREVIEW_OUT="$PWD/a11y/screens" npx vitest run
src/preview/a11yScreens.test.tsx`) and runs Playwright + axe in real
Chromium with the full rule set, contrast and target-size included
(`cd a11y && npm ci && npx playwright install chromium && npx playwright
test`). CI runs it as the `a11y` job. Policy and exclusions:
`docs/ACCESSIBILITY.md`.

Guard tests to know about (they fail the build on drift, deliberately):
schema drift (`schema.graphql` vs the code-first schema), audit-declaration
coverage (every GraphQL root field and MCP tool declares `[AuditAction]` or
`[NoAudit]`), telemetry hygiene (sentinel content must never reach spans or
metrics — design.md §15), the converted-markdown and heading-anchor corpora
(byte-exact cross-language contracts; `.gitattributes` pins them to LF —
don't "fix" their line endings).

## Changing the GraphQL schema

```bash
cd src/RocketWiki.Api
dotnet run schema export --output ../../schema.graphql
cd ../../web && npm run codegen
```

The drift test enforces the export; CI regenerates the web client from the
committed `schema.graphql`. (`schema-settings.json` is an incidental export
artifact and is gitignored.)

## CI (`.github/workflows/ci.yml`)

Five jobs on every push: `backend` (Release build + the container-free
tiers + corpus drift checks), `frontend` (codegen → lint → build → vitest +
anchor-corpus drift), `sqlserver` (the Testcontainers tier against the
FTS-enabled image, with a guard that fails the job if the tier skipped and
uploaded engine logs on failure), `a11y` (the real-Chromium Playwright + axe
pass over every screen in both themes — see the accessibility section
above), and `helm` (`helm lint --strict` plus a render of every
`FileStorage` provider and both failure modes the chart promises — the
render half exists because lint passes a template that emits the wrong env
var, which is exactly the bug adding a third provider uncovered).

All jobs are read-only (`permissions: contents: read`), bounded by
`timeout-minutes` so a hang fails in tens of minutes rather than the
six-hour default, and superseded runs cancel — the `sqlserver` job boots a
real database per run, so stacked runs on rapid pushes are the expensive
kind of waste. Action versions are kept current by `.github/dependabot.yml`
(grouped weekly); before it existed, `ci.yml` sat three majors behind
`docs.yml` on the actions they share.

## Optional local extras

- **Diagrams**: the draw.io editor needs `VITE_DRAWIO_URL` (fail-closed —
  unset means no external editor loads; viewing is unaffected). The AppHost
  provides a dev container; `https://embed.diagrams.net` is a dev-only
  opt-in the UI visibly flags.
- **GitLab integration**: set `GitLab:BaseUrl` (API config) to an
  in-network GitLab, then each user pastes a `read_api` PAT under Settings.
  A local `gitlab/gitlab-ce` container works but is heavyweight; nothing in
  the test suites needs it.
- **Gravatar endpoint**: `Avatars:GravatarEndpointEnabled=true` exposes
  `GET /avatar/{hash}` (deliberately unauthenticated — that's the protocol;
  read design.md §19 before enabling anywhere shared).
- **Semantic search**: configure the `embeddings` connection string or the
  `Ai:` section (design.md §9.2); unconfigured means keyword-only,
  structurally.
- **Ask the wiki**: on top of the semantic-search config above, set
  `Ai:ChatModel` (or the Aspire `assistant` connection string) to name the
  chat model (design.md §9.5). Unconfigured means `askWiki` answers
  `NOT_CONFIGURED`, structurally — no chat client is even registered.
- **Browser telemetry**: `VITE_OTEL_EXPORTER_OTLP_ENDPOINT` (note the
  Aspire dashboard's OTLP/**HTTP** port is 18890; 18889 is gRPC).

## Deployment

Not a local concern — see `deploy/README.md` (Helm chart, air-gapped image
paths, restore drill), and its own "what has never been verified" list.
