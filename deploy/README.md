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
docker build -f Dockerfile.web \
  --build-arg VITE_OIDC_AUTHORITY=https://keycloak.internal/realms/rocketwiki \
  --build-arg VITE_OIDC_CLIENT_ID=rocketwiki-web \
  -t rocketwiki/web:0.1.0 .
```

Tag versions deliberately — `latest` and `imagePullPolicy: IfNotPresent`
together are a classic stale-image trap on air-gapped nodes.

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
   are the spec to reproduce). Inside the network boundary, behind TLS.
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
  to a fetched `config.json` (a web/ change).

## Telemetry (design.md §15 "Telemetry is not audit")

Off by default and fail-closed: with `api.otel.enabled: false` the
`OTEL_EXPORTER_OTLP_ENDPOINT` variable is absent and ServiceDefaults
registers **no exporter at all** — telemetry cannot leave the pod. Enabling
it requires an endpoint, which must be a collector **inside the network
boundary**, same rule as every other service. Traces and logs carry no page
content, no search text, no attribute values by design — and none of it is,
or may ever become, the audit record (§7 owns that).
