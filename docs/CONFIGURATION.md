# RocketWiki — Configuration reference

Every configuration key the system reads, with its default, what **unset**
means, and the owning `design.md` section. Enumerated from source (the "Read
at" column names the file that actually reads each key), not from memory —
if this table and the code ever disagree, the code wins and this file has a
bug. Last verified against source: 2026-09-05 (the API/frontend tables against
2026-08-24; the web container's nginx runtime env and the operator CLIs'
connection string against 2026-08-30; the "Protective markings" section
against 2026-09-04, the day the selector-eligibility and clearance gates left
the engine; the instance/database, Keycloak and AI tables and the new "Feature
flags" section against 2026-09-05, the day every remaining key moved onto an
options class).

**Every key the API reads is bound to an options class** (`AddOptions<T>()
.Bind(section)`, most with `ValidateDataAnnotations().ValidateOnStart()`), and
the "Read at" column names it. Two moments of reading exist, and the tables
say which applies: a key is **eager** when it decides what gets *registered*
before the host is built (whether an AI client exists at all; the feature
flags), and is then bound off the builder through the same options class —
one definition of shape and defaults, read early; every other key is
**lazy**, resolved through `IOptions<T>` where it is consumed, so a value that
arrives with the built host (a test host's configuration is the canonical
case) is the value in force. Connection strings are the one exception to the
options rule: they stay on `GetConnectionString`, the API Aspire injects them
through.

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
| `Instance:Id` | `standalone` | This instance's sync identity is `standalone`; spaces whose `OriginInstanceId` differs are replicas (read-only). **Validated at startup**: an empty value fails the host (`Instance:Id must be a non-empty instance identifier`) — a blank id would compare every space's origin against nothing, the silent fail-open the Helm values schema also refuses | `Identity/InstanceOptions.cs`, bound in `Program.cs`; **lazy** — resolved into the `InstanceIdentity` singleton and stamped into the DbContext options through EF's `ConfigureDbContext` hook, never captured off the builder | §12 |
| `ConnectionStrings:rocketwiki` | *(none — Aspire-injected)* | No database. With `Database:MigrateOnStartup` true (the default) the API fails **loudly** at startup; with it false, every data query fails | `Program.cs` (`AddSqlServerDbContext`) | §14, §15 |
| `Database:MigrateOnStartup` | `true` (code default *and* shipped appsettings.json) | n/a (has a default). Set `false` in any multi-replica environment — two pods racing `MigrateAsync()` is a corruption risk; k3s runs migrations as a Job instead | `Hosting/DatabaseOptions.cs`, bound in `Program.cs`; **lazy** — read from the container after the host is built, immediately before the migration would run | §15 |

## Authentication (Keycloak)

All three `Keycloak:*` keys bind to `Identity/KeycloakOptions.cs` (registered
and **validated at startup** in `Program.cs`) and are **lazy**: the JWT bearer
handler reads them inside its options-configure callback at first use, and the
MCP protected-resource metadata reads them at options-resolution time. Both
call the one `KeycloakOptions.ResolveAuthority` — explicit `Keycloak:Authority`,
else the `keycloak` connection string plus `/realms/{Realm}`, else nothing — so
tokens can no longer validate against one realm while MCP discovery advertises
another (the two derivations used to be separate copies with a comment asking
that they be kept in step).

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| `ConnectionStrings:keycloak` | *(none — Aspire-injected)* | Combined with `Keycloak:Realm` to derive the JWT authority. Unset *and* no `Keycloak:Authority`: no authority — bearer validation cannot fetch JWKS metadata, so no authenticated request succeeds. Fail closed by construction | `Program.cs` and `Mcp/McpServerConfiguration.cs`, both through `KeycloakOptions.ResolveAuthority` | §11, §15 |
| `Keycloak:Realm` | `rocketwiki` (code and appsettings.json) | n/a (has a default). **Validated at startup**: must be non-empty — a blank realm derives an authority ending in `/realms/`, a deployment that boots and then rejects every sign-in | `Identity/KeycloakOptions.cs` | §11 |
| `Keycloak:Authority` | *(none)* | Authority is derived from the `keycloak` connection string + realm. Set this only when running standalone outside Aspire. **Validated at startup when set**: must be an absolute `http(s)://` URL (`Keycloak:Authority must be an absolute http(s) URL when set.`) | `Identity/KeycloakOptions.cs` | §11 |
| `Keycloak:Audience` | code fallback `rocketwiki`; **shipped appsettings.json sets `rocketwiki-api`** (must match the realm's audience mapper — see `src/RocketWiki.AppHost/keycloak/rocketwiki-realm.json`) | The code fallback applies only if appsettings is stripped. **Validated at startup**: must be non-empty | `Identity/KeycloakOptions.cs` | §11 |

## Protective markings

design.md §21.15 "Additional selectors": the compartment-style categories a
marking may carry beside its classification (`UK SECRET APPLE NORTH …`),
read from the `ProtectiveMarking:SelectorCategories` array, bound to
`ProtectiveMarkingOptions` and **validated at startup** (`ValidateOnStart`).
The catalog itself is built once, at first resolution, from the bound
options — not eagerly off the builder — and stamped into the `DbContext`
options through EF's provider-aware `ConfigureDbContext` hook, so a test
host's in-memory configuration is honoured too; production reads the same
values either way.

The categories are the instance's **vocabulary** of compartment values, and
nothing more. A page may carry at most one value per category. A reader sees
such a page only if a space **access grant** that matches them carries that
exact value (§6.4) — the grant is the whole test, and it fails closed.
**There is no site-wide eligibility attribute any more:** no token claim
gates a category, Keycloak needs no mapper per category, and the
classification level beside the selectors is display-only (it is never
compared against a person). Environment-variable form, one variable per
leaf, array indexes as path segments:

```
ProtectiveMarking__SelectorCategories__0__Name=FRUIT
ProtectiveMarking__SelectorCategories__0__Description=Fruit programme compartments
ProtectiveMarking__SelectorCategories__0__Values__0=APPLE
ProtectiveMarking__SelectorCategories__0__Values__1=BANANA
ProtectiveMarking__SelectorCategories__1__Name=REGION
ProtectiveMarking__SelectorCategories__1__Description=Regional releasability
ProtectiveMarking__SelectorCategories__1__Values__0=NORTH
ProtectiveMarking__SelectorCategories__1__Values__1=SOUTH
```

That exact pair is the dev vocabulary `aspire run` wires from
`src/RocketWiki.AppHost/AppHost.cs`. It is deliberately **not** in
`appsettings.Development.json`, which `WebApplicationFactory` test runs would
inherit. The Helm chart renders the same variables from
`api.protectiveMarking.selectorCategories` (`deploy/README.md`).

| Key | Default | Unset means | Read at | design.md |
|---|---|---|---|---|
| ⛔ `ProtectiveMarking:SelectorCategories` | *(none — deliberately)* | **No selector categories.** The marking control offers no selector pickers, and access grants carry no selector values. A page whose marking carries a selector for a category not configured here — it arrived by sync, or the category was removed — is visible to **nobody**: no grant can carry a value the catalog does not define, so nothing can satisfy the selector, and the denial names the unknown category rather than passing silently. Fail closed. **Invalid ⇒ the API fails to start**, naming the failing category (`Selector category 'FRUIT' …`) | `Markings/ProtectiveMarkingConfiguration.cs` — validated at startup, built at first resolution, stamped into the DbContext options via `ConfigureDbContext` | §21.15, §6.4 |
| `ProtectiveMarking:SelectorCategories:[n]:Name` | *(required per entry)* | Startup failure. A canonical upper-case token — `[A-Z0-9_-]`, at most 32 characters, unique across the list — that appears verbatim in marking labels | same | §21.15 |
| `ProtectiveMarking:SelectorCategories:[n]:Description` | *(none)* | No description beside the picker; nothing about access changes | same | §21.15 |
| `ProtectiveMarking:SelectorCategories:[n]:Values` | *(required per entry)* | Startup failure. Each value a canonical upper-case token like `Name`, unique within its category. A marking carries at most one value per category; an access grant may carry several, and a matching grant carrying the value is the only thing that lets a reader through it | same | §21.15, §6.4 |

A leftover `ProtectiveMarking:SelectorCategories:[n]:ClaimName` key from an
earlier configuration binds to nothing and is silently ignored — selector
eligibility is no longer a token claim, so there is no property for it to bind
to. Remove it; the Helm chart refuses it outright at lint time.

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
per value (`Ai/AiConnectionStringParser.cs`, called from
`Embeddings/EmbeddingPipelineConfiguration.cs` and
`Assistant/AssistantConfiguration.cs`).

The whole `Ai` section binds to one options class, `Ai/AiOptions.cs`
(registered and **validated at startup** by `Ai/AiConfiguration.cs`, ahead of
both features). Its keys fall into the two reading moments described at the
top of this file:

- **Eager** — the endpoint keys `Ai:BaseUrl`, `Ai:ApiKey`, `Ai:EmbeddingModel`,
  `Ai:ChatModel`. They decide whether a client, an options object and (for
  embeddings) a background job are *registered at all*, which has to happen
  before the host is built; they are bound off the builder through
  `AiOptions.BindEagerly`. In a `WebApplicationFactory` test host they are
  therefore reached with `UseSetting` (which travels as a command-line
  argument), never with `ConfigureAppConfiguration`.
- **Lazy** — every tuning key below (`Dimensions`, `PollSeconds`,
  `BatchSize`, `FailureBackoffSeconds`, `MaxAttempts`,
  `EmbeddingTimeoutSeconds`, `ChatTimeoutSeconds`, `MaxContextChars`,
  `MaxRetrievedPages`, `MaxQuestionChars`, `MaxOutputTokens`). Resolved from
  `IOptions<AiOptions>` when `EmbeddingOptions` / `AssistantOptions` are first
  needed, so late configuration is honoured.

Every tuning key **must be positive**; an explicit `0` or negative fails the
host at boot naming the key (`Ai:BatchSize must be a positive number of
pages.`). That replaces the earlier behaviour for `Ai:PollSeconds` and
`Ai:FailureBackoffSeconds`, where a non-positive value silently fell back to
the default — a zero poll interval or batch size is a configuration mistake,
and it is now reported where an operator is looking. Every default satisfies
its own annotation, so an instance with no `Ai` section at all still boots.

| Key | Default | Unset means | design.md |
|---|---|---|---|
| ⛔ `ConnectionStrings:embeddings` | *(none)* | With no `Ai:BaseUrl`/`Ai:EmbeddingModel` fallback either: **keyword-only search, structurally** — no generator, no background job, nothing registers. Shape: `Endpoint=…;Key=…;Model=…;Dimensions=…` (a bare URL is Endpoint-only). Also nothing registers when the `FeatureManagement:SemanticSearch` flag is off, whatever this says (see "Feature flags") | §9.2 |
| `Ai:BaseUrl` | *(none)* | Fallback endpoint for both AI clients when the connection string doesn't carry one (eager) | §9.2 |
| `Ai:ApiKey` | *(none)* | Keyless gateway — a placeholder credential is sent, which in-boundary gateways ignore (eager) | §9.2 |
| `Ai:EmbeddingModel` | *(none)* | With no `Model=` in the connection string: embeddings not configured (see above) (eager) | §9.2 |
| `Ai:Dimensions` | `1536` (the `vector(1536)` column's width, applied when neither the connection string's `Dimensions=` nor this key is set) | n/a (has a default). Must match the `vector(1536)` column — a contradicting value **fails startup** (`EmbeddingDimensionsStartupCheck`) before a single doomed write. **Validated at startup**: must be positive | §9.3 |
| `Ai:PollSeconds` | `30` s (via `EmbeddingOptions.PollIntervalOrDefault`) | n/a. Background job's scan interval. **Validated at startup**: must be positive (a `0` used to fall back to the default silently; it now fails the host) | §9.2 |
| `Ai:BatchSize` | `16` | n/a. Max pages (re-)embedded per job run. **Validated at startup**: must be positive | §9.2 |
| `Ai:FailureBackoffSeconds` | `300` s / 5 min (via `FailureBackoffOrDefault`) | n/a. Retry delay for a failed page. **Validated at startup**: must be positive | §9.2 |
| `Ai:MaxAttempts` | `5` | n/a. Consecutive failures on one revision before it is quarantined and the scan moves on (editing the page re-arms it). **Validated at startup**: must be positive | §9.2 |
| `Ai:EmbeddingTimeoutSeconds` | `30` s (via `RequestTimeoutOrDefault`) | n/a. Per-call network timeout on the embedding endpoint; no SDK retries. **Validated at startup**: must be positive | §9.2 |
| ⛔ `ConnectionStrings:assistant` | *(none)* | With no `Ai:ChatModel` fallback either: `askWiki` answers `NOT_CONFIGURED`, structurally — no chat client registers, retrieval is never touched. Shape: `Endpoint=…;Key=…;Model=…`. Also nothing registers when the `FeatureManagement:AskWiki` flag is off, whatever this says | §9.5 |
| ⛔ `Ai:ChatModel` | *(none)* | Its absence is what "assistant not configured" *means* (eager) | §9.5 |
| `Ai:ChatTimeoutSeconds` | `30` | n/a. One attempt, no retries; an ask degrades to `UNREACHABLE`, never hangs. **Validated at startup**: must be positive | §9.5 |
| `Ai:MaxContextChars` | `24000` | n/a. Cap on context text sent to the model per ask. **Validated at startup**: must be positive | §9.5 |
| `Ai:MaxRetrievedPages` | `8` | n/a. Permission-filtered hits retrieval asks `ISearchService` for. **Validated at startup**: must be positive | §9.5 |
| `Ai:MaxOutputTokens` | `800` | n/a. Cap on the answer the model may generate; without one the only bound was the 30 s network timeout. **Validated at startup**: must be positive | §9.5 |
| `Ai:MaxQuestionChars` | `2000` | n/a. Longest question accepted; over it, `askWiki` answers `QUESTION_TOO_LONG` before retrieval and before anything is sent. **Validated at startup**: must be positive. **The only key in this table the SPA can read** — `assistantStatus.maxQuestionChars` reports it (null when the assistant is unconfigured or its flag is off), so the ask page can warn while a question is being typed rather than only after it is refused | §9.5 |

## Feature flags

The six features that are optional by configuration — and only those six —
also have an explicit off-switch each, read through Microsoft.FeatureManagement
from the `FeatureManagement` section (`Features/RocketWikiFeatures.cs` names
them; `Features/FeatureFlagConfiguration.cs` wires them). Environment-variable
form is the usual double underscore: `FeatureManagement__AskWiki=false`. The
Helm chart carries them as `api.features.*` (`deploy/README.md`).

Three rules, each enforced by a test (`FeatureFlagTests`,
`FeatureFlagOffStateTests`, `FeatureFlagNameAgreementTests`):

- **Unset means ON.** The library's own default for a flag the configuration
  does not mention is *off*; RocketWiki's definition provider inverts that for
  exactly these six names, so an upgrade onto this version is a no-op on any
  existing configuration, and a misspelt flag is *ignored* (feature stays on)
  rather than silently off. Unknown names keep the library's behaviour.
- **A flag is an additional, independent off-switch; it turns nothing on.**
  `false` means off regardless of what is configured — a demo can silence the
  assistant without deleting its connection string. `true` with nothing
  configured still means not-configured: a flag alone registers no client,
  maps no endpoint, and sends content nowhere, because there is nowhere for it
  to go. The flag is checked first and the configuration second, so the two
  conditions are ANDed.
- **Off never changes the GraphQL schema.** Every field exists whichever way a
  flag is set, and a disabled feature answers the *same shape it already
  answers when unconfigured* (`assistantStatus.configured`,
  `gitlabStatus.configured`, `syncStatus.enabled` tell the SPA, which hides or
  explains the surface). `FeatureFlagOffStateTests` compares the SDL served with
  every flag off against `schema.graphql`.

**Read once, at startup.** All six are evaluated into one snapshot before the
host is built, because three of them decide what gets *registered* (the chat
client, the embedding pipeline, the MCP endpoint), and every other surface
reads that same snapshot — so a flag never flips one surface live while a
registration decided at boot stays put. Change a flag, restart the API; the
Helm chart rolls the pod. Write a flag as a plain boolean (or the library's
explicit `EnabledFor: [{ Name: AlwaysOn }]` form). A definition that uses a
time-window, percentage or targeting *filter* is refused at boot, naming the
filter: a switch evaluated once has no honest answer for "until 17:00".

| Key | Default | Unset means | Off means | Read at | design.md |
|---|---|---|---|---|---|
| `FeatureManagement:AskWiki` | `true` | On | No chat client or `AssistantOptions` register — the unconfigured container. `askWiki` answers `NOT_CONFIGURED`; `assistantStatus` reports `configured:false`, `maxQuestionChars:null`; the SPA shows its not-available copy. Nothing leaves the process | `Assistant/AssistantConfiguration.cs` (eager, registration) | §9.5 |
| `FeatureManagement:SemanticSearch` | `true` | On | No embedding generator, indexer, background job or dimension check register — keyword-only search, structurally, and no page content travels to the endpoint. `askWiki` retrieval (if on) is keyword-only too | `Embeddings/EmbeddingPipelineConfiguration.cs` (eager, registration) | §9.2, §9.3 |
| `FeatureManagement:GitLab` | `true` | On | `GitLabOptions.IsConfigured` is false whatever `GitLab:BaseUrl` says, at the one seam every GitLab field reads: `gitlabStatus` reports `configured:false`, `baseUrl:null`; `gitlabIssue`/`gitlabIssues`/`gitlabFile` answer `NOT_CONFIGURED`; the SPA hides every GitLab affordance. Stored tokens are untouched and the token mutations keep working, as when unconfigured | `GitLab/GitLabOptions.cs`, via `GitLab/GitLabConfiguration.cs` | §18 |
| `FeatureManagement:Mcp` | `true` | On | **Not mapped**: neither the MCP services nor the `/mcp` route are registered, so `/mcp` answers 404 and the RFC 9728 discovery metadata (`/.well-known/oauth-protected-resource`) is not served — nothing advertises a server that is not there (the HealthEndpoints precedent: absent, not refusing). GraphQL is unaffected | `Program.cs` (eager, registration and mapping) | §8 |
| `FeatureManagement:CoEditing` | `true` | On | **Refusing, silently**: `JoinEditSession` answers the same `null` every refusal answers, before any permission work, so the SPA falls back to the solo editor; `PushUpdate`/`PushAwareness`/`ReseedEditSession` are membership-gated and therefore no-ops. Not audited (no access decision was made — the not-found arm makes the same call); counted as `rocketwiki.coedit.outcome=disabled`. Presence and notifications share the hub and keep working | `RealTime/NotificationsHub.EditSessions.cs` | §8 |
| `FeatureManagement:Sync` | `true` | On | `setSpaceExported(exported: true)` refuses with a `Validation` error naming this key; un-flagging stays allowed so sync can be wound down without switching it back on. `syncStatus.enabled` reports false and the admin page says so, while still reporting the durable outbox/import state. **Deliberately unaffected**: the replica read-only invariant (a compliance rule, not a feature), the outbox journal for spaces already flagged exported (stopping it would fork low and high the moment the flag came back), and the `RocketWiki.Sync` CLI — a separate operator tool that reads no API configuration; not scheduling it is the operator's own off-switch for bundles | `GraphQL/Mutation.Spaces.cs`, `GraphQL/Query.SyncStatus.cs` | §12 |

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
| `VITE_API_TARGET` (dev server only) | `http://localhost:5079` | Dev proxy target. `launchSettings.json` is gitignored, so a fresh clone gets no launch profile and `dotnet run` binds Kestrel's default instead — start the API with `ASPNETCORE_URLS=http://localhost:5079` to match. Under Aspire the API container is pinned to this same port, so the default already matches | `vite.config.ts` |

## The web container (nginx runtime env, not `VITE_*`)

Read by `deploy/docker/nginx/default.conf.template` through the nginx image's
envsubst step — **runtime** values, unlike everything in the table above.
Two of them exist only because the SPA's external origins are baked in at
build time and nginx has no way to read them back. The Aspire AppHost sets all
three for the dev stack's web container (`AppHost.cs`: the API's
container-network address, and the Keycloak and draw.io origins it baked in).

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
