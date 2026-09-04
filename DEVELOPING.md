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
# Backend: build + the whole SQLite-tier suite (~2,400 tests, about a minute)
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
auto-imported: seven users covering the rule engine's edge cases, all password
`RocketWiki!Dev1` — see `src/RocketWiki.AppHost/keycloak/README.md`), MinIO,
and a draw.io container for the diagram editor, then the API with connection
strings injected. The Aspire dashboard URL is printed at startup; it also
receives all OpenTelemetry (traces/metrics/logs) automatically.

The AppHost also injects the **dev selector catalog**
(`ProtectiveMarking:SelectorCategories`, design.md §21.15) into the API: two
categories, `FRUIT` (values `APPLE`, `BANANA`) and `REGION` (values `NORTH`,
`SOUTH`). A category names no Keycloak attribute: a space's access grants
alone decide which values a user holds there, so to read a FRUIT-marked page
a dev user needs an access grant carrying that value and nothing on their
account. (The realm's `fruit` attribute and the per-category eligibility gate
it fed were removed on 2026-09-04 — see
`src/RocketWiki.AppHost/keycloak/README.md`.) It lives in `AppHost.cs` rather
than `appsettings.Development.json` so the `WebApplicationFactory` test tier
never picks it up by accident.

**The GraphQL IDE.** Nitro (Hot Chocolate's built-in IDE) is served at the API's
`/graphql` in Development — open that URL in a browser and you get a schema
browser and a query console. It is switched off in every other environment,
explicitly rather than by relying on the package default, because this
application holds classified content and an IDE is a schema browser plus a query
console handed to whoever can reach the endpoint (`Program.cs`, `ModifyServerOptions`).

Queries you run there travel the same authenticated path the SPA's do, so an
unauthenticated session sees exactly what an unauthenticated SPA would: the
absent-shaped empty answers. To run anything as a real user you need a token —
see the PKCE note in the Keycloak section of this file.

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

SQL Server has the same "only on a fresh volume" rule about its `sa` password,
which is why `AppHost.cs` pins one (`RocketWiki-dev-sa-1`) instead of letting
Aspire generate it. `MSSQL_SA_PASSWORD` is read only when the engine
initializes a new master database; after that the volume's copy wins. A
generated password therefore works until the value Aspire cached in user
secrets changes, and then the stack half-starts forever: SQL Server is
"healthy", the API sits in `WaitFor(sql)`, and the only evidence is
`Login failed for user 'sa'` inside the container's log. If you hit this on a
volume whose data you want to keep, reset the password against the *stopped*
volume rather than deleting it — `mssql-conf` refuses to run while `sqlservr`
holds the instance, so it needs its own container:

```bash
docker run --rm --user root \
  -v <the-sql-data-volume>:/var/opt/mssql \
  -e MSSQL_SA_PASSWORD='RocketWiki-dev-sa-1' \
  --entrypoint /opt/mssql/bin/mssql-conf <the-sql-image> set-sa-password
```

> **Retired caveat (design.md §16):** this section used to warn that
> `aspire run` had never actually been executed. It has, on 2026-08-28, from
> empty volumes: containers up, migrations applied to a real SQL Server 2025,
> every dev user logged in through PKCE, attachments round-tripped through
> MinIO, telemetry captured off the wire. See the README's status section for
> what that run verified — and for the eight defects it found, which is the
> honest argument for doing it sooner.

### Backend standalone (no Docker)

```bash
# launchSettings.json is gitignored, so a fresh clone has no launch profile and
# Kestrel binds its own default port. Ask for 5079 explicitly — that is the port
# the SPA dev proxy (VITE_API_TARGET) is configured against.
ASPNETCORE_URLS=http://localhost:5079 dotnet run --project src/RocketWiki.Api
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

Marking fixtures in the API tier (design.md §21.15), worth knowing before
you write a test that touches a selector:

- `RocketWikiApiFactory` configures its own selector catalog — the dev pair
  `FRUIT` (`APPLE`, `BANANA`) and `REGION` (`NORTH`, `SOUTH`) plus a third,
  `SENTINEL` category whose only value is the telemetry-hygiene sentinel
  `ZZSENTINELSELECTORZZ`, so a page nobody is granted can exist for the
  hygiene sweeps. The factory stamps the catalog into the DbContext options
  itself (`UseSelectorCatalog`), because its replacement `AddDbContext`
  registration bypasses `Program.cs`'s wiring.
- `SetTestUser(...)` takes `groups:`, `nationality:` and `roles:`; the
  `claims:` parameter is the escape hatch for any other claim. There is no
  clearance and no selector-claim parameter any more — nothing on the token
  gates a level or a category — so the way to give a test user a selector is
  an access grant carrying it, and the way to deny them one is to withhold
  that grant (design.md §21.15).
- A raw MCP POST in a test must send `Accept: application/json,
  text/event-stream`, or the server answers a JSON-RPC "Not Acceptable"
  before any tool runs and an "the sentinel is absent" assertion passes
  vacuously — one such test was found that way.
- `KeycloakClaimParityTests` pins the dev realm to exactly the three claims
  the API reads — `groups`, `nationality` and `roles` — and fails if a mapper
  for any of them is missing from `rocketwiki-realm.json`. It no longer reads
  `AppHost.cs`: a selector category names no claim, so adding one needs no
  mapper.

## Changing the GraphQL schema

```bash
cd src/RocketWiki.Api
dotnet run schema export --output ../../schema.graphql
cd ../../web && npm run codegen
```

The drift test enforces the export; CI regenerates the web client from the
committed `schema.graphql`. (`schema-settings.json` is an incidental export
artifact and is gitignored.)

## Adding or upgrading a NuGet package

The solution uses **Central Package Management**: every version lives in
`Directory.Packages.props` at the repo root, and project files reference
packages with no `Version` attribute at all.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Some.Package" Version="1.2.3" />

<!-- the project that needs it -->
<PackageReference Include="Some.Package" />
```

Putting a `Version` back on a `PackageReference` is a restore **error**
(NU1008), so the two halves cannot drift. Upgrading a package shared by six
projects is now one edit rather than six, and the rationale for a pin lives
in one place next to the version it explains — read the comments there before
bumping ImageSharp, SQLitePCLRaw or the `Microsoft.Extensions.*` line, each of
which is pinned for a stated reason.

Transitive pinning is deliberately **off**; the two security overrides stay
direct references, because running the tier that references them is what
proves the pinned version actually loads. Dependabot understands this layout
and keeps raising the same grouped minor/patch PRs against it.

One MSBuild trap, learned by shipping it: **`--` is illegal inside an XML
comment.** Writing a double hyphen as an em-dash in `Directory.Packages.props`
makes the file unparseable, and the failure does not name it — every project
reports `NU1015: PackageReference items do not have a version specified`,
because central package management stays switched on while the `PackageVersion`
items silently vanish. If you ever see NU1015 across the whole solution at once,
check the props file parses before checking anything else.

### Upgrades that are currently blocked

Both were attempted on 2026-08-28 and backed out; neither is stale-pin inertia.

- **graphql 16 → 17** breaks codegen. `@graphql-codegen/typescript-urql`
  (through `visitor-plugin-common`) declares a peer range topping out at
  graphql ^16, npm dedupes 17 into that slot anyway, and `npm run codegen`
  then dies with `Cannot read properties of undefined (reading 'some')`. It
  unblocks when the codegen plugins ship graphql 17 support — nothing in this
  repo needs changing.
- **SixLabors.ImageSharp 3 → 4** fails the build from the package's own
  targets: *"No Six Labors license found."* That is a licensing decision for
  whoever operates this deployment (design.md §19), not a maintenance chore,
  so the major is excluded in `.github/dependabot.yml` rather than re-proposed
  every week.

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
- **Selector categories** (design.md §21.15): a standalone API run configures
  none — no selector pickers, grants carry no values, and a page that
  *carries* a selector (one imported from a bundle, say) is visible to nobody,
  structurally. Set `ProtectiveMarking:SelectorCategories` (see
  `docs/CONFIGURATION.md`) to match what the AppHost injects, e.g.
  `ProtectiveMarking__SelectorCategories__0__Name=FRUIT`,
  `…__0__Values__0=APPLE`, `…__0__Values__1=BANANA`. There is no `ClaimName`
  key — a category names no Keycloak attribute (design.md §21.15) — and a
  leftover one from an older environment binds to nothing. Invalid values
  fail the host at startup rather than silently configuring nothing.

## Deployment

Not a local concern — see `deploy/README.md` (Helm chart, air-gapped image
paths, restore drill), and its own "what has never been verified" list.
