# RocketWiki — Configuration reference

Every configuration key the system reads, with its default, what **unset**
means, and the owning `design.md` section. Enumerated from source (the "Read
at" column names the file that actually reads each key), not from memory —
if this table and the code ever disagree, the code wins and this file has a
bug. Last verified against source: 2026-08-24.

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

## File storage

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| `FileStorage:Provider` | `FileSystem` (unset/empty selects it) | Filesystem provider. Any value other than `FileSystem`/`S3` **fails startup** — no silent fallback | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:FileSystem:Root` | *(none)* | With the FileSystem provider selected: `InvalidOperationException` when the provider is constructed — the root must be configured. Directory is created on first use | `Storage/FileSystemFileStorage.cs` | §10 |
| `FileStorage:S3:ServiceUrl` | *(none)* | AWS SDK default endpoint resolution (i.e. real AWS S3). Set it for MinIO/Ceph/any S3-compatible endpoint. **When set** it must be an absolute URL including scheme, or startup fails | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:S3:Bucket` | *(none)* | No bucket — S3 operations fail | `Storage/S3FileStorage.cs` | §10 |
| `FileStorage:S3:ForcePathStyle` | `true` | n/a (has a default). Required true for MinIO/Ceph/most non-AWS stores | `Storage/FileStorageOptions.cs` | §10 |
| `FileStorage:S3:AccessKey` / `SecretKey` | *(none)* | AWS SDK default credential chain | `Storage/ServiceCollectionExtensions.cs` | §10 |
| `FileStorage:S3:Region` | *(none)* | No auth region set; only meaningful against real AWS S3 / SigV4-validating providers | `Storage/ServiceCollectionExtensions.cs` | §10 |
| *(missing)* `FileStorage:S3` section with `Provider=S3` | — | `InvalidOperationException` at startup | `Storage/ServiceCollectionExtensions.cs` | §10 |

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
| `VITE_OIDC_AUTHORITY` | `http://localhost:8080/realms/rocketwiki` | The dev default — point at the real realm everywhere else | `src/auth/oidcConfig.ts` |
| `VITE_OIDC_CLIENT_ID` | `rocketwiki-web` | n/a (has a default) | `src/auth/oidcConfig.ts` |
| `VITE_OIDC_REDIRECT_URI` | `<origin>/auth/callback` | n/a (derived from the page origin) | `src/auth/oidcConfig.ts` |
| `VITE_OIDC_POST_LOGOUT_REDIRECT_URI` | `<origin>` | n/a (derived) | `src/auth/oidcConfig.ts` |
| `VITE_GRAPHQL_URL` | `/graphql` | Same-origin (dev proxy / prod nginx) | `src/graphql/client.ts` |
| `VITE_SIGNALR_NOTIFICATIONS_URL` | `/hubs/notifications` | Same-origin; the ONE hub (notifications + presence + co-editing) | `src/realtime/transports.ts`, `src/telemetry/config.ts` |
| `VITE_FAKE_REALTIME` | *(unset = real transports)* | Real SignalR. Only the exact string `true` swaps in in-memory fakes — a typo can't silently disconnect you | `src/realtime/transports.ts` |
| ⛔ `VITE_DRAWIO_URL` | *(none — deliberately)* | No external diagram editor loads; viewing is unaffected (diagrams are inline page content). Editing posts diagram content to this URL, hence no default | `src/editor/drawio/drawioConfig.ts` |
| ⛔ `VITE_OTEL_EXPORTER_OTLP_ENDPOINT` | *(none — deliberately)* | Browser telemetry entirely off; the tracing SDK chunk is never even downloaded. Malformed values also mean off | `src/telemetry/config.ts` |
| `VITE_OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | *(none)* | Traces URL is `<base>/v1/traces`; set this to use a verbatim URL instead | `src/telemetry/config.ts` |
| `VITE_OTEL_EXPORTER_OTLP_HEADERS` | *(none)* | No collector auth headers (`key=value,key2=value2` form) | `src/telemetry/config.ts` |
| `VITE_OTEL_SERVICE_NAME` | `rocketwiki-web` | n/a (has a default). Worth overriding per instance so replica traces are distinguishable | `src/telemetry/config.ts` |
| `VITE_APP_VERSION` | *(none)* | `service.version` omitted entirely | `src/telemetry/config.ts` |
| `VITE_API_TARGET` (dev server only) | `http://localhost:5079` | Dev proxy targets the API's standalone launch profile; under Aspire read the dynamic port off the dashboard and set this | `vite.config.ts` |
