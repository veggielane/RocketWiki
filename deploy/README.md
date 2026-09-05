# Deploying RocketWiki on k3s

This directory is design.md §16 milestone 9's "reviewed manifests/Helm
in-repo": hand-authored, owned artifacts (per §15 — "Generate once, then own
them"), not generated output. It contains:

```
deploy/
├── README.md                     this file
├── docker/nginx/
│   └── default.conf.template     nginx config for the web image (envsubst
│                                 template — see its header)
└── helm/rocketwiki/              the Helm chart: api + web Deployments,
                                  Services, Traefik ingress, EF migration
                                  Job, values with every knob commented
```

Plus, at the repo root (they need the repo root as build context):
`Dockerfile.api`, `Dockerfile.web`, `.dockerignore`.

## What has never been verified

**Read this first.** The environment this was authored in has no container
runtime, no cluster, and no reachable registry (README.md "Current status" —
the standing caveat applies with full force here):

- **Neither Dockerfile has ever been built.** No image exists. The
  multi-stage builds, the `dotnet ef migrations bundle` step, the nginx
  envsubst templating — all correct by documentation and inspection only.
- **No helm release has ever been installed.** `helm lint` passes and
  `helm template` renders valid YAML for the default and the exercised
  non-default value combinations (verified with Helm v4.2.4, plus a
  mechanical YAML re-parse of the rendered output) — that is the *entire*
  extent of verification. No manifest has been applied to any cluster.
- **The EF migrations bundle has never executed** — and the checked-in
  migration itself has never been applied to a real SQL Server (it carries
  TODO-flagged SQL Server-only features: FTS, `vector`, partitioning).
- **The probe endpoints are config-gated but have never answered a real
  kubelet** (see "Probes", below), the SignalR path has never carried a real
  WebSocket, and nothing has ever authenticated against a real Keycloak.

Assume nothing here works until the container-runtime task resumes and each
of these is exercised. The first `docker build`, first `helm install`, and
first migration run are all expected to surface fixable bugs; the point of
this package is that they'll be fixable in reviewed, owned files.

## Building the images

From the repo root (the build context for both):

```sh
# API — publishes src/RocketWiki.Api and bakes the EF migrations bundle.
docker build -f Dockerfile.api -t rocketwiki/api:0.1.0 .

# Web — Vite build baked with the target environment's OIDC settings.
# Vite INLINES VITE_* at build time: this image is permanently bound to this
# authority/client id. A different Keycloak realm means a rebuild, not a
# config change (limitation documented in Dockerfile.web).
# VITE_OIDC_AUTHORITY is REQUIRED — the build refuses without it, and refuses
# anything that is not an http(s) URL. See the next section.
docker build -f Dockerfile.web \
  --build-arg VITE_OIDC_AUTHORITY=https://keycloak.internal/realms/rocketwiki \
  --build-arg VITE_OIDC_CLIENT_ID=rocketwiki-web \
  -t rocketwiki/web:0.1.0 .
```

Tag versions deliberately — `latest` and `imagePullPolicy: IfNotPresent`
together are a classic stale-image trap on air-gapped nodes.

### `VITE_OIDC_AUTHORITY` is required, at both ends

The SPA fails closed without it: no realm, no sign-in, and a "Sign-in is not
configured" screen instead of a guess. Correct — and on its own it only moves
the failure from *users redirected to localhost* to *the image is a brick
nobody notices until first login*. So both ends refuse:

- **The image will not build without it.** `Dockerfile.web`'s
  `oidc-authority-check` stage rejects an omitted authority *and* one that
  isn't an `http(s)` URL, because `readOidcConfig` rejects the latter too and a
  typo therefore ships the same unusable image as an omission. Deliberately
  building an unusable image (to smoke-test nginx routing with no realm to
  hand) needs `--build-arg ALLOW_UNCONFIGURED_OIDC=1`, which prints a loud
  warning. It is checkable on its own: `docker build -f Dockerfile.web --target
  oidc-authority-check .` costs one alpine layer, and CI runs exactly that.
- **The chart will not render without it.** `web.oidcAuthority` is required and
  pattern-checked. Its default is a placeholder on the reserved `.invalid` TLD,
  which can never resolve, and the install NOTES say so by name.

### The web image's Content-Security-Policy must match its build args

The SPA is served with a real CSP (see
`deploy/docker/nginx/default.conf.template` for the full directive-by-directive
reasoning). Everything in it is fixed in the image **except** the two
directives that have to name external origins, because those origins are
baked into the bundle at *build* time — Vite inlines `VITE_*`, so the running
nginx has no way to discover what the image was built against.

`connect-src` is **derived** from `web.oidcAuthority`, so the origin sign-in
needs and the origin the policy permits are one value stated once:

```yaml
web:
  # The realm the image was BUILT with. connect-src becomes
  # "'self' https://keycloak.internal" automatically.
  oidcAuthority: "https://keycloak.internal/realms/rocketwiki"
  csp:
    # Only if the bundle also talks to something else — an OTLP collector, a
    # second IdP. Setting this means you own the WHOLE list, Keycloak included.
    connectSrc: ""
    # Nothing to derive this from: draw.io is its own build arg.
    frameSrc: "https://drawio.internal https://keycloak.internal"
```

| Build arg | Chart value | Why |
|---|---|---|
| `VITE_OIDC_AUTHORITY` | `web.oidcAuthority` (→ `connect-src`) | `oidc-client-ts` fetches discovery, token and userinfo over XHR from the Keycloak origin. |
| `VITE_DRAWIO_URL` | `web.csp.frameSrc` | The diagram editor is an `<iframe>` on that origin. |
| `VITE_OTEL_EXPORTER_OTLP_ENDPOINT` | `web.csp.connectSrc` (override) | Browser traces POST to the collector origin. |

`'self'` already covers same-origin `ws`/`wss`, so `/hubs` needs nothing extra.
Note the derivation takes the URL's **origin**, not the realm path: a CSP
source is an origin, and `https://keycloak.internal/realms/rocketwiki` as
written would be matched as a path prefix nobody ever requests.

**A forgotten `frameSrc` breaks the diagram editor loudly at first use.** That
is the deliberate trade: the alternative default — permit every origin — would
fail silently and permanently, which is not the posture anything else here
takes. An empty `frameSrc` is rejected by the values schema, because
`frame-src ;` is an empty source list — the same as `'none'`, but by accident
rather than by decision.

The other headers need no configuration: `X-Frame-Options: DENY` plus
`frame-ancestors 'none'` (the wiki is never framed), `X-Content-Type-Options:
nosniff`, and `Referrer-Policy: no-referrer` — the last is stricter than the
usual `strict-origin-when-cross-origin` on purpose, because page URLs here
carry space keys and slugs and a slug can disclose as much as a title.

**HSTS is automatic and conditional.** nginx emits
`Strict-Transport-Security` only when `X-Forwarded-Proto: https` arrives, so a
plain-HTTP deployment (the chart's `ingress.tls: []` default) never advertises
it and a TLS-terminated one gets it with nobody remembering to switch it on.
No `includeSubDomains` and no `preload`: this is an internal wiki whose
siblings on the same parent domain are other people's services, and
`includeSubDomains` would be making a decision about them from here.

## Getting images to an air-gapped network (design.md §15)

Two supported paths; both end with the same chart values.

**Path A — private registry mirror.** Push both images to the in-network
registry, then set one value:

```sh
docker tag rocketwiki/api:0.1.0 registry.internal:5000/rocketwiki/api:0.1.0
docker push registry.internal:5000/rocketwiki/api:0.1.0   # repeat for web
```

```yaml
# values override
global:
  imageRegistry: registry.internal:5000
imagePullSecrets:            # only if the mirror requires auth
  - name: internal-registry-cred
```

(k3s can alternatively be pointed at the mirror cluster-wide via
`/etc/rancher/k3s/registries.yaml`; that's a node-level operator choice this
chart doesn't manage.)

**Path B — image tarballs, no registry at all.** On the connected side:

```sh
docker save rocketwiki/api:0.1.0 rocketwiki/web:0.1.0 -o rocketwiki-images.tar
```

Carry the tarball across the gap, then on each k3s node:

```sh
sudo k3s ctr images import rocketwiki-images.tar
```

Leave `global.imageRegistry` empty (preloaded images are found by their
original names) and set both `pullPolicy` values to `Never`, so a missing
image is an immediate error instead of a hang against a registry that
doesn't exist:

```yaml
api: { image: { pullPolicy: Never } }
web: { image: { pullPolicy: Never } }
```

## Prerequisites the chart does NOT provide

Deliberately (design.md §15/§17 — external services stay external):

1. **SQL Server, outside the cluster** (§17: the lower-risk default —
   backups and restore drills beat in-cluster elegance). The chart only ever
   sees a connection string. It will not run SQL Server, and there is no
   values knob to make it.
2. **Keycloak** with a production-configured `rocketwiki` realm (the dev
   realm's mappers documented in `src/RocketWiki.AppHost/keycloak/README.md`
   are the spec to reproduce: `groups`, `nationality`, `roles` and the API
   audience — nothing per selector category, and no clearance; see
   "Protective-marking selector categories" below). Inside the network
   boundary, behind TLS.
3. **Somewhere for attachment bytes**, which is a real choice, not a default:
   an **S3-compatible object store** (MinIO/Ceph/…) with a bucket; or, for
   `api.fileStorage.provider=FileSystem`, a deliberately-created PVC the
   chart will mount but never create; or `provider=SqlServer`, which needs
   **neither** — blobs go in the database you already have. That last option
   is not recommended in general (transaction-log churn, backups that grow
   with attachments, buffer-pool pressure, no CDN path) but is the only one
   where a single backup covers content and attachments consistently, which
   on a single-node air-gapped k3s is sometimes worth more than all of it.
   See design.md §10 and docs/CONFIGURATION.md.
4. **The configuration Secret** (next section).

### Create the configuration Secret

The chart references an operator-created Secret (default name
`rocketwiki-api`) and injects it wholesale as environment; its keys are .NET
configuration env-var names. Placeholders here — real values never appear in
this repo:

```sh
kubectl create namespace rocketwiki

kubectl -n rocketwiki create secret generic rocketwiki-api \
  --from-literal=ConnectionStrings__rocketwiki='Server=sql.internal;Database=RocketWiki;User Id=rocketwiki;Password=CHANGE-ME;Encrypt=True;TrustServerCertificate=False' \
  --from-literal=Keycloak__Authority='https://keycloak.internal/realms/rocketwiki' \
  --from-literal=Keycloak__Audience='rocketwiki-api' \
  --from-literal=FileStorage__S3__AccessKey='CHANGE-ME' \
  --from-literal=FileStorage__S3__SecretKey='CHANGE-ME'
```

**The AI and integration endpoints go in this same Secret, and every one of them
is fail-closed** — absent means the feature is not registered at all, so leaving
one out gives you a silently feature-less install rather than an error:

```sh
  # Semantic search (design.md §9.2). Without it, search is keyword-only.
  --from-literal=ConnectionStrings__embeddings='Endpoint=http://llm-gateway:8000/v1;Key=CHANGE-ME;Model=text-embedding-3-small'
  # The askWiki assistant (§9.5). Needs an endpoint AND a model, or it answers
  # NOT_CONFIGURED. The model can ride in the connection string as Model=… instead
  # of the separate key; either is fine, and the separate key is the clearer one.
  --from-literal=ConnectionStrings__assistant='Endpoint=http://llm-gateway:8000/v1;Key=CHANGE-ME'
  --from-literal=Ai__ChatModel='llama-3.3-70b-instruct'
  # GitLab integration (§18). Absent = off.
  --from-literal=GitLab__BaseUrl='https://gitlab.internal'
```

**If you configure the assistant, configure embeddings too.** Without an embedding
endpoint, retrieval falls back to keyword-only (design.md §9.2, structurally) — and
both keyword legs are built for keywords, not sentences: SQL Server ANDs every term,
so every word of the question must appear on one page. A question phrased as a
sentence therefore returns `NO_RESULTS` most of the time. `askWiki` still works and
still refuses to answer ungrounded, so this is a quality limit rather than a
correctness one, but an assistant configured without embeddings will look broken to
its users. This is a known limitation with a deferred fix, not a misconfiguration.

The two `FileStorage__S3__*` keys are only needed with `provider=S3`; drop
them for `FileSystem`. With `provider=SqlServer` you add nothing — blobs
share `ConnectionStrings__rocketwiki` — unless you want them in a separate
database, which is one more key in this same Secret:

```sh
  --from-literal=FileStorage__SqlServer__ConnectionString='Server=sql.internal;Database=RocketWikiBlobs;User Id=rocketwiki;Password=CHANGE-ME;Encrypt=True;TrustServerCertificate=False'
```

That account needs `CREATE TABLE` on first use, because the blob table is
created on demand and sits outside the EF migration chain (design.md §10).
A locked-down deployment can pre-create it from the DDL in
docs/CONFIGURATION.md and grant only DML.

Without it, the migration Job fails config validation and the install stops
before anything rolls — that's fail-closed, not a bug.

### Protective-marking selector categories (design.md §21.15)

Not in the Secret: they are policy, not credentials, and belong in the values
file where a diff shows them. `api.protectiveMarking.selectorCategories` lists
the categories this instance recognises (`values.yaml` documents the shape and
the reasoning); each renders into the api container's environment as
`ProtectiveMarking__SelectorCategories__<n>__*`. A category is the instance's
**vocabulary** of compartment values and nothing more: a page carries at most
one value per category, and a reader sees it only if a space **access grant**
that matches them carries that exact value. There is no site-wide eligibility
attribute — Keycloak needs no mapper per category, and the values schema
refuses the retired `claimName` key (a values file from before 2026-09-04 that
still carries it fails `helm lint` and `helm install`; delete the key). Empty —
the default — means no categories: no pickers in the marking control, no
selector values on access grants, and a page whose marking carries a selector
for an unlisted category is visible to **nobody** (no grant can carry a value
the catalog does not define, and the denial names the unknown category). A
low/high pair must list every category that travels between them, spelled
identically. The API validates the list at startup and refuses to start on an
invalid one, naming the offending index.

```yaml
api:
  protectiveMarking:
    selectorCategories:
      - name: FRUIT
        description: Fruit programme compartments
        values: [APPLE, BANANA]
      - name: REGION           # description is optional; a grant
        values: [NORTH, SOUTH] # carrying NORTH is what confers NORTH
```

### Feature flags (docs/CONFIGURATION.md "Feature flags")

Also in the values file, not the Secret: `api.features` carries the six
optional features' off-switches — `askWiki`, `semanticSearch`, `gitLab`,
`mcp`, `coEditing`, `sync` — each rendering as `FeatureManagement__<Name>` on
the api container. All default to `true`, which is also what the API assumes
when the variable is absent, so an untouched values file changes nothing. A
flag is an off-switch only: `false` silences a feature whatever is configured
for it (its connection string or URL in the Secret can stay — that is the
point, a demo instance can hide the assistant without anyone deleting a
credential), and `true` with nothing configured is still "not configured".
Off never changes the GraphQL schema; each feature answers the shape it
already answers when unconfigured. `values.schema.json` refuses unknown keys
here so a misspelt flag fails `helm lint` rather than rendering a variable the
API would ignore — and an ignored flag means on. Read once at startup: a
change rolls the pod.

```yaml
api:
  features:
    askWiki: false        # a demo without the assistant; the connection string stays put
```

## Install / upgrade / rollback

```sh
helm upgrade --install rocketwiki deploy/helm/rocketwiki \
  -n rocketwiki -f my-values.yaml
```

Order of events on every install/upgrade: the **pre-install/pre-upgrade Job**
applies EF migrations to completion (running the `efbundle` baked into the
api image — mechanism justified in `templates/migrate-job.yaml`), and only
then do the Deployments roll. If the release hangs, look at the Job first:

```sh
kubectl -n rocketwiki logs job/rocketwiki-migrate
```

A failed migration Job is deliberately left behind for inspection (the
hook-delete-policy cleans up only successful runs).

The Job passes **no** `--connection` argument. It used to, expanded from the
Secret — which put the database credentials into the container's argv, where
`kubectl describe pod` shows them, `/proc/<pid>/cmdline` exposes them to
anything else in the pod, and the container runtime records them. `efbundle`
now reads `ROCKETWIKI_CONNECTIONSTRING` from the environment instead (via
`RocketWikiDbContextFactory`), so the pod spec carries only a `secretKeyRef`
and the value never leaves the container's own environment. If the variable is
somehow empty the factory falls back to a `localhost` design-time placeholder,
which cannot resolve inside the Job's container: it fails rather than migrating
something unintended.

The two operator CLIs (`RocketWiki.Importer`, `RocketWiki.Sync`) take the same
value the same three ways, and prefer the same one:

```sh
# Best: nothing secret in argv at all.
export ROCKETWIKI_CONNECTIONSTRING='Server=…;Encrypt=True;TrustServerCertificate=False'
RocketWiki.Sync import --bundle /transfer --instance-id high --origin-instance-id low \
                       --attachments-root /var/rocketwiki/attachments

# Or from a mounted secret file (trailing newline is trimmed).
RocketWiki.Sync import --connection-string-file /run/secrets/rocketwiki-db …

# --connection-string still works, and still puts the credential in argv.
```

**Rollback — the honest part.** `helm rollback rocketwiki <revision>` rolls
back *manifests only*. It does **not** roll back the database schema: EF
down-migrations are not wired into any path here, and reversing a migration
against production data is not something to improvise during an incident.
Treat rollback as safe only when the upgrade being reverted contained no
migration, or a migration that old code tolerates (additive column, new
table). Otherwise the recovery path is the restore drill below — which is
why it gets drilled.

## The restore drill (design.md §16, milestone 9)

The §15 position is that backups and restore drills matter more than
elegance, so the drill is part of the deliverable. It has **never been
performed** — the first run will find problems, which is what drills are for.

**What a full backup is** (all four, or it isn't a backup):

1. **SQL Server** — native `BACKUP DATABASE` on the external server (full +
   log per local policy). Everything that is the wiki — pages, revisions,
   rules, the append-only audit tables — lives here.
2. **The object store** — the attachments bucket (`mc mirror` or
   equivalent). DB rows reference these blobs; a DB-only restore yields a
   wiki whose attachments 404 (surfaced as the `BlobMissing` structured 500,
   README.md).
3. **Kubernetes config** — the `rocketwiki-api` Secret and your values file.
   The Secret exists only in the cluster; export it (`kubectl get secret
   rocketwiki-api -o yaml`) to the same controlled store as the DB backups.
4. **Keycloak** — realm export, per Keycloak's own procedures. Identity is
   part of the system's state even though it's outside this chart.

**The drill** (scratch environment — a spare k3s VM is enough):

1. Restore the SQL backup to a scratch SQL Server; restore the bucket to a
   scratch store.
2. Create the namespace + Secret pointing at the scratch endpoints.
3. `helm install` the same chart version and images (via either offline
   path).
4. Verify, minimum bar: migration Job completes as a no-op against the
   restored schema; login round-trips through Keycloak; a known page renders
   with correct permission filtering (§6); a known attachment downloads
   (blob + row agree); the audit log shows the drill's own reads (§7 —
   the drill is itself an auditable access).
5. Time it. The measured number is the real RPO/RTO input, not a guess.

Cadence: on any schema-migration release, and otherwise on a fixed calendar
(quarterly at minimum). Log each drill's date, chart/image versions, and
time-to-verified.

## Probes

`/health` and `/alive` are mapped in Development and, outside it, behind the
explicit `HealthEndpoints:Enabled` opt-in (default false —
`src/RocketWiki.ServiceDefaults/Extensions.cs`). The chart now defaults to
**HTTP probes** (`api.probes.mode: http`): `/alive` → liveness, `/health` →
readiness, with the chart setting `HealthEndpoints__Enabled` on the api
container. Safe because probes come from the kubelet over the pod network
and the ingress never routes either path to the api Service (they fall into
the SPA catch-all). `mode: tcp` remains as the fallback for operators who
want the endpoints unmapped even in-pod.

## Other known follow-ups (tracked, not hidden)

- **First-run verification** of everything in "What has never been
  verified" — the highest-value unblocking action, same as README.md says.
- **Scaling out**: `api.replicaCount` is locked to 1 (schema + template
  guard) until a Redis SignalR backplane lands in src/ (§15). Remove the
  guard in the same change as the backplane, not before.
- **readOnlyRootFilesystem**: not set (both .NET and nginx need writable
  paths; solvable with emptyDir mounts but unverifiable here — see
  values.yaml `podSecurityContext`).
- **Attachment size limit**: nginx's `client_max_body_size 100m` is
  currently the *only* upload cap in the system — the API enforces none.
  Needs a product decision and an API-side limit; the nginx line then
  matches it instead of defining it.
- **Runtime SPA config**: if rebuilding the web image per environment
  becomes a real burden, move the OIDC settings from build-time `VITE_` vars
  to a fetched `config.json` (a web/ change). That would also let the CSP's
  external origins be derived rather than restated (see the CSP section
  above), which is the only part of that policy an operator can get wrong.

## Network posture: what this chart does and does not promise

Stated plainly, because "the chart hardens it" is the kind of assumption that
is only discovered to be false during an incident.

**TLS terminates at the ingress, and the chart does not configure it.**
`ingress.tls` defaults to `[]` and `ingress.host` to `""`, which serves the
wiki over **plain HTTP on any host**. That default is not a recommendation —
it is the only thing a chart can honestly default to when certificates are an
operator concern (k3s's default cert, a corporate CA Secret, cert-manager).
For any real deployment, set both:

```yaml
ingress:
  host: wiki.internal
  tls:
    - secretName: rocketwiki-tls
      hosts: [wiki.internal]
```

Until you do, the HSTS header is correctly absent (see the CSP section) and
bearer tokens cross the network in the clear.

**In-cluster hops are not encrypted by this chart, and two of them carry
credentials.** The web→api hop is plain HTTP by design (nginx to a ClusterIP
Service). The api→S3 hop follows whatever `api.fileStorage.s3.serviceUrl`
says, and the default example is `http://` — which puts the S3 access key on
the pod network on every request. Point it at `https://` against a MinIO/Ceph
endpoint with a certificate the pod trusts. The api→SQL Server and
api→Keycloak hops *are* covered: `Encrypt=True;TrustServerCertificate=False`
in the connection string (see the Secret section) and `RequireHttpsMetadata`
outside Development, respectively.

**There is no `NetworkPolicy` in this chart, deliberately.** A useful policy
has to name the egress this deployment actually needs — SQL Server, Keycloak,
the object store, the OTLP collector — and every one of those is an address
the chart never sees. A default-deny policy templated from values would either
be wrong (blocking a hop the operator configured) or vacuous (allowing
everything). It is left to the cluster's own policy layer, where those
addresses are known. If the cluster has a CNI that enforces policy, a
default-deny-ingress for the namespace plus explicit allows for the ingress
controller → web/api is the shape to write; the pods' own ports are 8080
only.

**`automountServiceAccountToken` is not set on any pod.** None of the three
containers talks to the Kubernetes API, so the token is unused — but it is
still projected into every pod, which is one credential more than any of them
needs. Setting `automountServiceAccountToken: false` on the pod specs is a
one-line hardening that could not be verified running here, and is left as a
follow-up rather than shipped unverified.

## Telemetry (design.md §15 "Telemetry is not audit")

Off by default and fail-closed: with `api.otel.enabled: false` the
`OTEL_EXPORTER_OTLP_ENDPOINT` variable is absent and ServiceDefaults
registers **no exporter at all** — telemetry cannot leave the pod. Enabling
it requires an endpoint, which must be a collector **inside the network
boundary**, same rule as every other service. Traces and logs carry no page
content, no search text, no attribute values by design — and none of it is,
or may ever become, the audit record (§7 owns that).
