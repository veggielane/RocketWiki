# RocketWiki — Configuration reference

Every configuration key the system reads, with its default, what **unset**
means, and the owning `design.md` section. Enumerated from source (the "Read
at" column names the file that actually reads each key), not from memory —
if this table and the code ever disagree, the code wins and this file has a
bug. Last verified against source: 2026-09-02 (the API/frontend tables against
2026-08-24; the web container's nginx runtime env and the operator CLIs'
connection string against 2026-08-30; the "Protective markings" section
against 2026-09-02).

Two conventions to know before reading:

- **Fail-closed keys** are marked ⛔. For these, *unset is a supported,
  deliberate state*: the feature is absent, structurally — no default
  endpoint, no fallback, no guess (design.md §15). Setting them is an
  operator's explicit decision, usually because content or credentials
  travel to the configured destination.
- Under **Aspire** (`aspire run`), connection strings are injected by
  service discovery from the AppHost topology — never hand-written into
  appsettings (design.md §15 "config by reference"). The `Keycloak:*` /
  `Ai:*` fallbacks exist for running the API standalone, outside Aspire.

Environment-variable form: `Database:MigrateOnStartup` is set as
`Database__MigrateOnStartup` (double underscore), standard .NET
configuration binding.

## Instance identity and database

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| `Instance:Id` | `standalone` | This instance's sync identity is `standalone`; spaces whose `OriginInstanceId` differs are replicas (read-only) | `Program.cs` | §12 |
| `ConnectionStrings:rocketwiki` | *(none — Aspire-injected)* | No database. With `Database:MigrateOnStartup` true (the default) the API fails **loudly** at startup; with it false, every data query fails | `Program.cs` (`AddSqlServerDbContext`) | §14, §15 |
| `Database:MigrateOnStartup` | `true` (code default *and* shipped appsettings.json) | n/a (has a default). Set `false` in any multi-replica environment — two pods racing `MigrateAsync()` is a corruption risk; k3s runs migrations as a Job instead | `Program.cs` | §15 |

## Authentication (Keycloak)

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| `ConnectionStrings:keycloak` | *(none — Aspire-injected)* | Combined with `Keycloak:Realm` to derive the JWT authority. Unset *and* no `Keycloak:Authority`: no authority — bearer validation cannot fetch JWKS metadata, so no authenticated request succeeds. Fail closed by construction | `Program.cs`, `Mcp/McpServerConfiguration.cs` (both derivations deliberately kept in step — see the comment there) | §11, §15 |
| `Keycloak:Realm` | `rocketwiki` (code and appsettings.json) | n/a (has a default) | same two files | §11 |
| `Keycloak:Authority` | *(none)* | Authority is derived from the `keycloak` connection string + realm. Set this only when running standalone outside Aspire | same two files | §11 |
| `Keycloak:Audience` | code fallback `rocketwiki`; **shipped appsettings.json sets `rocketwiki-api`** (must match the realm's audience mapper — see `src/RocketWiki.AppHost/keycloak/rocketwiki-realm.json`) | The code fallback applies only if appsettings is stripped | `Program.cs` | §11 |

## Protective markings

design.md §21.15 "Additional selectors": the compartment-style categories a
marking may carry beside its classification (`UK SECRET APPLE NORTH …`),
read from the `ProtectiveMarking:SelectorCategories` array, bound to
`ProtectiveMarkingOptions` and **validated at startup** (`ValidateOnStart`).
The catalog itself is built once, at first resolution, from the bound
options — not eagerly off the builder — and stamped into the `DbContext`
options through EF's provider-aware `ConfigureDbContext` hook, so a test
host's in-memory configuration is honoured too; production reads the same
values either way. Reading a page then
needs, per selector on it, eligibility for the category (site-wide, from the
token) *and* a grant of that value in the space (§6.4) — two gates, both
fail closed. Environment-variable form, one variable per leaf, array indexes
as path segments:

```
ProtectiveMarking__SelectorCategories__0__Name=FRUIT
ProtectiveMarking__SelectorCategories__0__Description=Fruit programme compartments
ProtectiveMarking__SelectorCategories__0__ClaimName=fruit
ProtectiveMarking__SelectorCategories__0__Values__0=APPLE
ProtectiveMarking__SelectorCategories__0__Values__1=BANANA
ProtectiveMarking__SelectorCategories__1__Name=REGION
ProtectiveMarking__SelectorCategories__1__ClaimName=
ProtectiveMarking__SelectorCategories__1__Values__0=NORTH
ProtectiveMarking__SelectorCategories__1__Values__1=SOUTH
```

That exact pair is the dev vocabulary `aspire run` wires from
`src/RocketWiki.AppHost/AppHost.cs`, matching the dev realm's `fruit`
attribute. It is deliberately **not** in `appsettings.Development.json`,
which `WebApplicationFactory` test runs would inherit. The Helm chart renders
the same variables from `api.protectiveMarking.selectorCategories`.

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| ⛔ `ProtectiveMarking:SelectorCategories` | *(none — deliberately)* | **No selector categories.** The marking control offers no selector pickers, and access grants carry no selector values. A page whose marking carries a selector for a category not configured here — it arrived by sync, or the category was removed — is visible to **nobody**: fail closed, and the denial names the unknown category rather than passing silently. **Invalid ⇒ the API fails to start**, naming the failing category (`Selector category 'FRUIT' …`) | `Markings/ProtectiveMarkingConfiguration.cs` — validated at startup, built at first resolution, stamped into the DbContext options via `ConfigureDbContext` | §21.15, §6.4 |
| `ProtectiveMarking:SelectorCategories:[n]:Name` | *(required per entry)* | Startup failure. A canonical upper-case token — `[A-Z0-9_-]`, at most 32 characters, unique across the list — that appears verbatim in marking labels | same | §21.15 |
| `ProtectiveMarking:SelectorCategories:[n]:Description` | *(none)* | No description beside the picker; nothing about access changes | same | §21.15 |
| `ProtectiveMarking:SelectorCategories:[n]:ClaimName` | *(none)* | **Every user is eligible** for the category; the space grant alone decides. When set, the token attribute of that name must equal `yes` (trimmed, case-insensitive) for the user to be eligible — any other value, or its absence, is not eligible (fail closed). Needs a matching single-valued mapper in Keycloak (§11.5; `src/RocketWiki.AppHost/keycloak/README.md`). `groups`, `sub`, `clearance` and `nationality` are refused as claim names | same | §21.15, §11.5 |
| `ProtectiveMarking:SelectorCategories:[n]:Values` | *(required per entry)* | Startup failure. Each value a canonical upper-case token like `Name`, unique within its category. A marking carries at most one value per category; an access grant may carry several | same | §21.15, §6.4 |

## File storage

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| `FileStorage:Provider` | `FileSystem` (unset/empty selects it) | Filesystem provider. Any value other than `FileSystem`/`S3`/`SqlServer` **fails startup** — no silent fallback | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:FileSystem:Root` | *(none)* | With the FileSystem provider selected: `InvalidOperationException` when the provider is constructed — the root must be configured. Directory is created on first use | `Storage/FileSystemFileStorage.cs` | §10 |
| `FileStorage:S3:ServiceUrl` | *(none)* | AWS SDK default endpoint resolution (i.e. real AWS S3). Set it for MinIO/Ceph/any S3-compatible endpoint. **When set** it must be an absolute URL including scheme, or startup fails | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:S3:Bucket` | *(none)* | No bucket — S3 operations fail | `Storage/S3FileStorage.cs` | §10 |
| `FileStorage:S3:ForcePathStyle` | `true` | n/a (has a default). Required true for MinIO/Ceph/most non-AWS stores | `Storage/FileStorageOptions.cs` | §10 |
| `FileStorage:S3:AccessKey` / `SecretKey` | *(none)* | AWS SDK default credential chain | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:S3:Region` | *(none)* | No auth region set; only meaningful against real AWS S3 / SigV4-validating providers | `Storage/ServiceCollectionExtensions.cs` | §10 |
| *(missing)* `FileStorage:S3` section with `Provider=S3` | — | `InvalidOperationException` at startup | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:SqlServer:ConnectionString` | *(none)* | With the SqlServer provider selected: falls back to `ConnectionStrings:rocketwiki` — blobs share the application database, which is the reason to pick this provider. With neither set, `InvalidOperationException` at construction naming both keys. Set it to keep blob churn out of the application's transaction log | `Storage/SqlServerFileStorage.cs` | §10 |
| *(schema)* `dbo.FileStorageBlobs` | — | Created on first use, idempotently, **outside the EF migration chain**. The application account needs `CREATE TABLE` once; a locked-down deployment can pre-create it from the DDL below and grant DML only | `Storage/SqlServerFileStorage.cs` | §10 |

### `FileStorage:Provider=SqlServer` — why this is not the default

Blobs in the database cost you: transaction-log churn on every upload (a
100 MiB attachment is 100 MiB of log), backups that grow with attachments
rather than with content, buffer-pool pressure as large reads evict hot
pages, per-GB licensing and storage at database rates, and no CDN or offload
path — every byte is served by the API from the engine.

Choose it anyway when a single consistent backup is worth more than all of
that: air-gapped or single-container installs, dev/test environments that
shouldn't need a MinIO or a volume, and restore drills you want to be one
restore rather than two with a reconciliation step in the middle. Attachment
behaviour is otherwise identical — the same `canView` check, the same §7
audit row, the same streaming download; no presigned URLs exist on any
provider.

Pre-creating the table (optional):

```sql
CREATE TABLE dbo.FileStorageBlobs
(
    [Key] nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Content] varbinary(max) NOT NULL,
    [ContentType] nvarchar(127) NULL,
    [ByteLength] bigint NOT NULL,
    [CreatedAtUtc] datetime2(3) NOT NULL,
    CONSTRAINT [PK_FileStorageBlobs] PRIMARY KEY CLUSTERED ([Key])
);
```

The binary collation is required, not cosmetic: without it a case-insensitive
database would treat two distinct storage keys as one row.

## Upload limits (binary HTTP surface)

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| `Attachments:MaxSizeBytes` | `104857600` (100 MiB — matches the nginx `client_max_body_size` in front of the API) | n/a (has a default). Enforced in the upload route before any blob write or row insert; over-limit is a structured 413, deliberately not audited. **Validated at startup**: a non-positive value fails the host with `Attachments:MaxSizeBytes must be a positive number of bytes.` | `Attachments/AttachmentOptions.cs`, bound in `Program.cs` | §10 |
| `Avatars:MaxSizeBytes` | `5242880` (5 MiB) | n/a (has a default). Same layered 413 enforcement. **Validated at startup** (must be positive) | `Avatars/AvatarOptions.cs`, bound in `Program.cs` | §19 |
| ⛔ `Avatars:GravatarEndpointEnabled` | `false` | The **unauthenticated** Gravatar/Libravatar endpoint `GET /avatar/{hash}` 404s for every hash. Enabling it is the codebase's one deliberate exception to "all access requires sign-in" — anyone on the network can fetch avatars and probe email hashes | `Avatars/AvatarOptions.cs` | §19 |
| `Emojis:MaxSizeBytes` | `262144` (256 KiB) | n/a (has a default). Applied twice: to the raw upload (413) and to the re-encoded stored bytes (validation error). **Validated at startup** (must be positive) | `Emojis/EmojiOptions.cs`, bound in `Program.cs` | §19 |

## Real-time co-editing (operational caps, not authorization)

All five bound from the `CoEdit` section (`RealTime/CoEditOptions.cs`; read
in `Program.cs` both for options binding and to derive the SignalR transport
message-size limit). All have defaults, so unset means the defaults below.
design.md §8.

Like the three upload caps, these bind through
`AddOptions().Bind().ValidateDataAnnotations().ValidateOnStart()`: the four
byte caps must be positive or the host fails to start with
`CoEdit:<Name> must be a positive number of bytes.` `EmptySessionGrace` is
deliberately not validated — a `TimeSpan` range attribute drags in
`TypeDescriptor` and is trim-hostile.

| Key | Default | What it bounds |
|---|---|---|
| `CoEdit:UpdateMaxBytes` | `524288` (512 KiB) | One Yjs `PushUpdate` payload; oversized is dropped and counted, never relayed or logged |
| `CoEdit:AwarenessMaxBytes` | `16384` (16 KiB) | One awareness (caret/selection) payload |
| `CoEdit:SnapshotMaxBytes` | `4194304` (4 MiB) | One `ReseedEditSession` full-state snapshot |
| `CoEdit:LogCapBytes` | `8388608` (8 MiB) | Retained update log per session before the server demands save-and-reseed |
| `CoEdit:EmptySessionGrace` | `00:01:00` (60 s, TimeSpan binding) | How long an empty session's log survives before being dropped |

## GitLab integration

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| ⛔ `GitLab:BaseUrl` | *(none — deliberately)* | The feature is absent: every GitLab field answers `NOT_CONFIGURED`, and every GitLab affordance in the SPA vanishes. This URL is where users' GitLab PATs are sent, so a defaulted value would be a credential-exfiltration bug | `GitLab/GitLabOptions.cs` | §18, §15 |
| `GitLab:TimeoutSeconds` | `5` | n/a (has a default). Short by design: page-view-time fetches must degrade to placeholders, not hang | `GitLab/GitLabOptions.cs` | §18 |
| `GitLab:MaxFileBytes` | `524288` (512 KiB) | n/a (has a default). Cap on embedded repository file fetches | `GitLab/GitLabOptions.cs` | §18 |

## AI — embeddings (semantic search) and the assistant

Both endpoints are the same class of dependency: page content travels to
them, so they must live inside the network boundary (design.md §9.4) — which
is why both are ⛔ fail-closed with **no default endpoint**. Precedence is
identical for both: the Aspire connection string first, then the `Ai:*` keys
per value (`Embeddings/EmbeddingPipelineConfiguration.cs`,
`Assistant/AssistantConfiguration.cs`).

| Key | Default | Unset means | design.md |
|---|---|---|---|
| ⛔ `ConnectionStrings:embeddings` | *(none)* | With no `Ai:BaseUrl`/`Ai:EmbeddingModel` fallback either: **keyword-only search, structurally** — no generator, no background job, nothing registers. Shape: `Endpoint=…;Key=…;Model=…;Dimensions=…` (a bare URL is Endpoint-only) | §9.2 |
| `Ai:BaseUrl` | *(none)* | Fallback endpoint for both AI clients when the connection string doesn't carry one | §9.2 |
| `Ai:ApiKey` | *(none)* | Keyless gateway — a placeholder credential is sent, which in-boundary gateways ignore | §9.2 |
| `Ai:EmbeddingModel` | *(none)* | With no `Model=` in the connection string: embeddings not configured (see above) | §9.2 |
| `Ai:Dimensions` | `1536` | n/a (has a default). Must match the `vector(1536)` column — a contradicting value **fails startup** (`EmbeddingDimensionsStartupCheck`) before a single doomed write | §9.3 |
| `Ai:PollSeconds` | `30` s (via `EmbeddingOptions.PollIntervalOrDefault`) | n/a. Background job's scan interval; non-positive values fall back to the default | §9.2 |
| `Ai:BatchSize` | `16` | n/a. Max pages (re-)embedded per job run | §9.2 |
| `Ai:FailureBackoffSeconds` | `300` s / 5 min (via `FailureBackoffOrDefault`) | n/a. Retry delay for a failed page | §9.2 |
| ⛔ `ConnectionStrings:assistant` | *(none)* | With no `Ai:ChatModel` fallback either: `askWiki` answers `NOT_CONFIGURED`, structurally — no chat client registers, retrieval is never touched. Shape: `Endpoint=…;Key=…;Model=…` | §9.5 |
| ⛔ `Ai:ChatModel` | *(none)* | Its absence is what "assistant not configured" *means* | §9.5 |
| `Ai:ChatTimeoutSeconds` | `30` | n/a. One attempt, no retries; an ask degrades to `UNREACHABLE`, never hangs | §9.5 |
| `Ai:MaxContextChars` | `24000` | n/a. Cap on context text sent to the model per ask | §9.5 |
| `Ai:MaxRetrievedPages` | `8` | n/a. Permission-filtered hits retrieval asks `ISearchService` for | §9.5 |
| `Ai:MaxOutputTokens` | `800` | n/a. Cap on the answer the model may generate; without one the only bound was the 30 s network timeout | §9.5 |
| `Ai:MaxQuestionChars` | `2000` | n/a. Longest question accepted; over it, `askWiki` answers `QUESTION_TOO_LONG` before retrieval and before anything is sent. **The only key in this table the SPA can read** — `assistantStatus.maxQuestionChars` reports it (null when the assistant is unconfigured), so the ask page can warn while a question is being typed rather than only after it is refused | §9.5 |

## Health endpoints and telemetry

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| ⛔ `HealthEndpoints:Enabled` | `false` outside Development (Development always maps them) | `/health` and `/alive` are **not mapped** in production — they are unauthenticated and `/health` aggregates dependency state, which is free reconnaissance on an open network. The Helm chart sets it for kubelet HTTP probes and keeps both paths unrouted at the ingress | `ServiceDefaults/Extensions.cs` | §15 |
| ⛔ `OTEL_EXPORTER_OTLP_ENDPOINT` (env var) | *(none)* | **No OTLP exporter registers at all** — instruments still emit in-process (tests listen to them), but nothing leaves the process. No default endpoint exists because the collector must stay inside the network boundary | `ServiceDefaults/Extensions.cs` | §15 |

Stock ASP.NET Core keys also present in `appsettings.json` and read by the
framework, not by RocketWiki code: `Logging:LogLevel:*` (Default
Information, Microsoft.AspNetCore Warning — note §15's posture: the backend
itself logs almost nothing) and `AllowedHosts` (`*`).

## Frontend (`web/`, Vite)

All are **build-time** (`import.meta.env`) except `VITE_API_TARGET`, which
only the dev server reads. `web/.env.example` documents each with its full
caveats; this table is the summary. Defaults verified at the read sites
named.

| Key | Default | Unset means | Read at |
|---|---|---|---|
| `VITE_OIDC_AUTHORITY` | *(none — required)* | **Sign-in is disabled** and the app renders "Sign-in is not configured". Fails closed on purpose: a guessed authority sends every login to a host nobody chose | `src/auth/oidcConfig.ts` |
| `VITE_OIDC_CLIENT_ID` | `rocketwiki-web` | n/a (has a default) | `src/auth/oidcConfig.ts` |
| `VITE_OIDC_REDIRECT_URI` | `<origin>/auth/callback` | n/a (derived from the page origin) | `src/auth/oidcConfig.ts` |
| `VITE_OIDC_POST_LOGOUT_REDIRECT_URI` | `<origin>` | n/a (derived) | `src/auth/oidcConfig.ts` |
| `VITE_GRAPHQL_URL` | `/graphql` | Same-origin (dev proxy / prod nginx) | `src/graphql/client.ts` |
| `VITE_SIGNALR_NOTIFICATIONS_URL` | `/hubs/notifications` | Same-origin; the ONE hub (notifications + presence + co-editing) | `src/realtime/transports.ts`, `src/telemetry/config.ts` |
| `VITE_FAKE_REALTIME` | *(unset = real transports)* | Real SignalR. Only the exact string `true` swaps in in-memory fakes — a typo can't silently disconnect you | `src/realtime/transports.ts` |
| ⛔ `VITE_DRAWIO_URL` | *(none — deliberately)* | No external diagram editor loads; viewing is unaffected (diagrams are inline page content). Editing posts diagram content to this URL, hence no default | `src/editor/drawio/drawioConfig.ts` |
| ⛔ `VITE_OTEL_EXPORTER_OTLP_ENDPOINT` | *(none — deliberately)* | Browser telemetry entirely off; the tracing SDK chunk is never even downloaded. Malformed values also mean off | `src/telemetry/config.ts` |
| `VITE_OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | *(none)* | Traces URL is `<base>/v1/traces`; set this to use a verbatim URL instead | `src/telemetry/config.ts` |
| `VITE_OTEL_EXPORTER_OTLP_HEADERS` | *(none)* | No collector auth headers (`key=value,key2=value2` form). **Never a real secret** — every `VITE_*` value is inlined into the public bundle; terminate collector auth server-side | `src/telemetry/config.ts` |
| `VITE_OTEL_SERVICE_NAME` | `rocketwiki-web` | n/a (has a default). Worth overriding per instance so replica traces are distinguishable | `src/telemetry/config.ts` |
| `VITE_APP_VERSION` | *(none)* | `service.version` omitted entirely | `src/telemetry/config.ts` |
| `VITE_API_TARGET` (dev server only) | `http://localhost:5079` | Dev proxy target. `launchSettings.json` is gitignored, so a fresh clone gets no launch profile and `dotnet run` binds Kestrel's default instead — start the API with `ASPNETCORE_URLS=http://localhost:5079` to match. Under Aspire, read the dynamic port off the dashboard and set this | `vite.config.ts` |

## The web container (nginx runtime env, not `VITE_*`)

Read by `deploy/docker/nginx/default.conf.template` through the nginx image's
envsubst step — **runtime** values, unlike everything in the table above.
Two of them exist only because the SPA's external origins are baked in at
build time and nginx has no way to read them back.

| Key | Default | Unset means | Read at |
|---|---|---|---|
| `API_UPSTREAM` | `api:8080` | n/a (has a default; the Helm chart sets the api Service's DNS name) | `Dockerfile.web`, nginx template |
| `CSP_CONNECT_SRC` | `'self'` | Same-origin XHR/WebSocket only. **Must name the `VITE_OIDC_AUTHORITY` origin**, or sign-in fails at the first discovery fetch; add the OTLP collector origin too if browser telemetry is on | nginx template `Content-Security-Policy` |
| `CSP_FRAME_SRC` | `'none'` | No frames at all. **Must name the `VITE_DRAWIO_URL` origin** if the diagram editor is enabled | nginx template `Content-Security-Policy` |

Both CSP values are correct as they stand for an image built with neither
`VITE_OIDC_AUTHORITY` nor `VITE_DRAWIO_URL` — which is what an unconfigured
build is, since both fail closed. A forgotten value therefore breaks sign-in
loudly at the first attempt rather than silently permitting every origin. The
Helm chart carries them as `web.csp.connectSrc` / `web.csp.frameSrc`, and its
schema rejects empty strings (an empty `connect-src` blocks even same-origin
`/graphql`). Everything else in the policy — and the HSTS, `X-Frame-Options`,
`X-Content-Type-Options` and `Referrer-Policy` headers — needs no
configuration; see `deploy/README.md`.

## Operator CLIs (`RocketWiki.Importer`, `RocketWiki.Sync`) and the migration Job

| Key | Default | Unset means | Read at |
|---|---|---|---|
| `ROCKETWIKI_CONNECTIONSTRING` | *(none)* | The CLI falls back to `--connection-string-file`, then `--connection-string`; with none of the three it prints usage and exits 1. For the Helm migration Job the same variable is what `efbundle` runs against | `CliArgumentParser`, `SyncCliArgumentParser`, `RocketWikiDbContextFactory` |

`ConnectionStrings__rocketwiki` is also accepted by
`RocketWikiDbContextFactory` (the standard .NET spelling), so a Secret whose
key is already that name works with no mapping.

**Prefer the variable, or `--connection-string-file <path>`, over
`--connection-string <value>`.** A credential on the command line is readable
by every other local user (`ps`, `/proc/<pid>/cmdline`, Process Explorer) and
lands in shell history and crash dumps. The flag still works because scripted
invocations exist, not because it is a good idea.
