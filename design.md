# RocketWiki — Design Document

A self-hosted wiki to replace Confluence. WYSIWYG editing with Markdown as the storage
format, a .NET backend serving GraphQL, and a Vite/React/MUI frontend.

**Status:** Draft — under discussion

---

## 1. Goals

- Replace Confluence for internal documentation: spaces, page trees, rich editing,
  attachments, comments, search, and page history.
- Content stored as **Markdown** — portable, diffable, greppable, and never locked
  into a proprietary format again (the core pain of leaving Confluence).
- Familiar **WYSIWYG editing** so non-technical users never see raw Markdown unless
  they want to.
- Single sign-on via the existing **Keycloak** instance (OIDC).
- Simple to operate: one API container, one static frontend, SQL Server,
  S3-compatible object storage.
- Runs as **separated low/high instances** with serialized one-way content
  sync (low → high) across a controlled boundary; low-sourced content is
  read-only on high (§12). Content never flows high → low.
- **AI-ready without new trust**: semantic search (§9) and an MCP endpoint
  (§8) that lets assistants work with the wiki strictly under each user's
  own permissions.

### Non-goals (v1)

- Real-time collaborative editing (Google-Docs-style co-editing). Markdown storage
  makes this hard; v1 uses optimistic concurrency with conflict detection instead.
  The SignalR hub added for notifications is the intended transport for a Yjs/CRDT
  layer in v2 — deliberately a spike, not a commitment (§8).
- Plugin/macro marketplace. A small set of built-in blocks (callouts, code, tables,
  task lists) covers most Confluence macro usage.
- Public/anonymous wikis. All access requires sign-in.

---

## 2. Tech stack

| Layer | Choice | Notes |
|---|---|---|
| Backend | ASP.NET Core (.NET 10 LTS) | GraphQL API via Hot Chocolate |
| Orchestration | .NET Aspire | AppHost composes every resource; ServiceDefaults gives health checks, OpenTelemetry, service discovery (§15) |
| API | GraphQL (Hot Chocolate) | single `/graphql` endpoint; attachment binary over plain HTTP (§8) |
| ORM | EF Core | SQL Server in prod, SQLite provider in tests (§14) |
| Database | SQL Server 2025 | Full-Text Search + native `vector` type (§9) |
| AI | OpenAI-compatible endpoint | embeddings via `Microsoft.Extensions.AI`; must sit inside the security boundary (§9.4) |
| MCP | Official MCP C# SDK at `/mcp` | assistants act as the user (OAuth via Keycloak); same rules + audit (§8) |
| Real-time | SignalR (MessagePack) | notifications, presence + live pointers; future CRDT transport (§8) |
| Frontend | Vite + React + TypeScript | SPA |
| UI components | MUI (Material UI) | app shell, forms, dialogs; DataGrid for audit viewer + admin tables; rule builder built from MUI primitives |
| Editor | TipTap (ProseMirror) + Markdown serialization | See §4 |
| GraphQL client | urql + GraphQL Code Generator | typed hooks generated from `schema.graphql` |
| Auth | Keycloak via OIDC | Auth Code + PKCE in the SPA, JWT bearer to the API |
| Files | S3-compatible object storage | `IFileStorage` abstraction; filesystem provider for dev/tests (§10) |
| Instances | Standalone, or low/high pair | One-way serialized sync low → high (§12) |
| Deployment | Docker Compose now, **k3s** intended | offline-installable on separated networks; migrations as a Job, Redis backplane only if replicas > 1 (§15) |

---

## 3. Architecture overview

```
┌─────────────┐   OIDC (code + PKCE)   ┌──────────┐
│  React SPA  │ ◄────────────────────► │ Keycloak │
│ (Vite build)│                        └──────────┘
└──────┬──────┘                              ▲
       │ GraphQL + SignalR, Bearer JWT       │ JWKS / token validation
       ▼                                     │
┌─────────────────┐                          │
│ ASP.NET Core API│ ─────────────────────────┘
│                 │──► SQL Server (pages, revisions, metadata, FTS, vectors)
│                 │──► S3-compatible object storage (attachments)
│                 │──► OpenAI-compatible endpoint (embeddings, in-boundary)
└─────────────────┘
```

- The SPA is served as static files (nginx container or ASP.NET static hosting —
  either works; default to a small nginx container so the API stays a pure API).
- The API is stateless; all state lives in SQL Server and the attachment store.
- AI assistants connect to the same API at `/mcp`, authenticating through
  Keycloak as the user (§8) — a second client, not a second backend.
- The **Aspire AppHost** is the single definition of this diagram in code:
  it wires the API, the web app, SQL Server, object storage, Keycloak, and
  the embedding endpoint together, injecting connection details by service
  discovery rather than hand-maintained URLs (§15).

---

## 4. Content model: WYSIWYG over Markdown

The defining design decision. Users edit in a rich editor; the document is
serialized to Markdown for storage.

### Editor

- **TipTap** (React bindings over ProseMirror) with the `tiptap-markdown`
  extension for Markdown import/export.
- The editor loads Markdown → ProseMirror document, the user edits visually,
  and on save the document serializes back to Markdown.

### Supported content (v1)

Everything must round-trip through Markdown cleanly, so the feature set is
deliberately constrained to GitHub-Flavored Markdown plus a few extensions:

| Feature | Markdown representation |
|---|---|
| Headings, bold/italic/strike, lists, quotes, links | Standard GFM |
| Tables | GFM pipe tables (no merged cells — accepted limitation vs Confluence; column alignment `:---:` not supported in v1) |
| Code blocks with language | Fenced code blocks |
| Task lists | GFM `- [ ]` |
| Images / attachments | `![alt](attachment://{id})` — resolved to URLs at render time |
| Callouts (info/warning/note) | Directive syntax: `:::info … :::` |
| Page links | `[title](page://{id})` — stable across renames |
| Mentions | `@[display](user://{id})` |
| Diagrams | ` ```mermaid ` fenced block (source rendered client-side); ` ```drawio ` fenced block whose body is base64 of the diagrams.net editable-SVG export — one payload renders as an inert data-URI image and reloads into the embed editor. Both are plain fenced text to the serializer, sync bundles (§12), and the importer (§13); `drawio` is a reserved fence language. Per-diagram payload cap: 512 KB of base64 |

**Rule:** no editor feature ships unless it round-trips (Markdown → editor →
Markdown produces identical output). A round-trip test suite enforces this,
running content through a real editor instance — not just the parser and
serializer talking to each other, which would prove nothing.

**No underline.** GFM has no representation for it, so an underline button
would be an affordance whose formatting silently vanishes on save. It is
disabled deliberately, not overlooked.

### Canonical normalization (verified by the spike)

The round-trip holds for everything the editor can *produce*. Markdown
authored elsewhere may use equivalent alternate spellings that normalize to
one canonical form on first save:

| Input | Becomes | Why |
|---|---|---|
| `_x_`, `__x__` | `*x*`, `**x**` | the parser doesn't record which delimiter was used |
| `1.` repeated on every item | `1.` `2.` `3.` … | ProseMirror stores a list-level `start`, not per-item markers |
| loose lists (blank line before a nested sub-list) | tight lists (no blank line) | CommonMark's loose/tight distinction has no representation in the document model — ProseMirror list items are always block-wrapped, and the information is destroyed at parse time |

Neither is reachable from the editor UI, so day-to-day editing is unaffected.
It matters for **imported content** (§13): a migrated page reformats slightly
the first time someone saves it. Accepted — the alternative is preserving
byte-level authoring quirks the WYSIWYG editor cannot represent.

### Why not raw HTML or ProseMirror JSON?

Portability is a stated goal. Markdown files can be exported wholesale, read in
any tool, and diffed meaningfully in page history. The cost — no merged table
cells, limited layout — is acceptable.

---

## 5. Domain model

The shape and the reasoning live here; the table-level schema — every column,
key, index, and constraint — is in [`data-model.md`](data-model.md).

```
Space 1──* Page 1──* PageRevision
  │           │ 1──* Attachment
  │           │ 1──* Comment
  │           │ 1──* AccessRule (page restrictions)
  │           └──* Label (many-to-many)
  └── 1──* AccessRule (space grants)
User (provisioned just-in-time from OIDC claims; carries mirrored attributes)
AuditEvent (append-only; one per user action — see §7)
SyncOutboxEvent (append-only journal of changes to exported spaces — see §12)
PageEmbedding (per-chunk vectors for semantic search — see §9; derived, never synced)
```

- **Space** — top-level container (like a Confluence space). Key (short slug),
  name, description, homepage. Records its origin instance: native spaces are
  editable, replicas of a lower instance are read-only (§12); native spaces
  on low can be flagged *exported*.
- **Page** — belongs to a space, has a `ParentPageId` forming a tree. Carries a
  slug, current title, sort position, and a denormalized `CurrentContent` column
  (Markdown of latest revision) for full-text indexing.
- **PageRevision** — immutable. `PageId`, revision number, Markdown content,
  title at time of save, author, timestamp, optional edit summary. Every save
  creates one. History UI diffs revisions as Markdown text diffs.
- **Attachment** — file metadata (name, content type, size, uploader) + storage
  key. Belongs to a page. Blob bytes live in object storage (§10), not the DB.
- **Comment** — page-level threaded comments in v1 (body is Markdown too).
  Inline/anchored comments deferred to v2.
- **Label** — free-form tags, space-scoped, many-to-many with pages.
- **User** — local row created/updated on first login from token claims
  (`sub`, `email`, `name`, plus registered attributes such as nationality —
  see §6). Never stores credentials.
- **AccessRule** — an AND/OR rule expression attached to a space (granting a
  role) or a page (restricting an action). The heart of access control; see §6.
- **AuditEvent** — append-only record of every user action, reads included;
  see §7.

### Concurrency

Optimistic: the editor holds the revision number it loaded; the save fails
with a typed `StaleRevisionError` (§8) if a newer revision exists. The UI then
offers "view their changes / overwrite / copy my text". Good enough without
real-time sync.

---

## 6. Access control

Attribute-based access control (ABAC): rules are boolean expressions over the
user's **groups** and **attributes** (e.g. nationality), evaluated fresh on
every request. Designed for export-control-style requirements
("Engineering group AND nationality NZ or US") without Confluence's ACL maze.

### 6.1 The principal

Built per request from the validated access token:

```
Principal {
  userId:     sub claim
  groups:     groups claim            (managed in Keycloak)
  attributes: { nationality: "NZ", … } (from registered claims, see 6.2)
}
```

Rules always evaluate against the **token**, never the local mirror — a change
in Keycloak takes effect on the user's next token refresh, not next login.
The local `User` row mirrors claims at each request for display/admin UI only.

### 6.2 Attribute registry

A small config table declaring which claims become rule-usable attributes:

| Field | Example |
|---|---|
| Key | `nationality` |
| Claim name | `nationality` (Keycloak user attribute → protocol mapper → token) |
| Type | `string` or `string[]` (dual nationals need `string[]`) |
| Allowed values | ISO country codes (drives rule-builder dropdowns) |

Attributes are stored and managed in **Keycloak** (single source of truth);
RocketWiki only declares which ones exist and mirrors current values.
Nationality is sensitive personal data: mirrored values are visible to
instance admins only, and only registered attributes are ever stored.

### 6.3 Rule expressions

A rule is a JSON expression tree. Combinators: `allOf` (AND), `anyOf` (OR).
Conditions: `group`, `user`, `attr`, `everyone` (any authenticated user).
**No NOT and no deny rules** — allow-list thinking only.

```json
{
  "allOf": [
    { "group": "engineering" },
    { "anyOf": [
      { "attr": "nationality", "in": ["NZ", "US"] },
      { "group": "export-cleared" }
    ]}
  ]
}
```

**Fail closed:** a user with no `nationality` attribute matches no `attr`
condition; a malformed or unevaluable rule denies access and logs an error.

**Matching is exact (ordinal).** Group names, user ids, and attribute values
are compared byte-for-byte — no case folding, no trimming. Guessing at what
an admin "meant" is exactly the wrong instinct in an access-control engine.
Typos are prevented at the source instead: the rule builder (§6.6) offers
pickers for known groups and registered attribute values, so hand-typed
strings never reach a rule in normal use.

### 6.4 Space grants and page restrictions

Two kinds of `AccessRule`, with different semantics:

- **Space grants** — grant a role when the expression matches:
  `space-admin` ⊃ `editor` ⊃ `viewer` (each includes the ones below).
  Multiple grants OR together: your role is the highest whose expression you
  satisfy. An "open" space is just a `viewer` grant with `{ "everyone": true }`.
- **Page restrictions** — restrict-only, per action (`view`, `edit`). A
  restriction never widens access; it adds a further condition on top of the
  space role. Restrictions **accumulate down the page tree**: to act on a page
  you must satisfy the restrictions of the page *and every ancestor*.

Effective permission:

```
canView(page) = spaceRole ≥ viewer
                AND every view-restriction on page + ancestors passes
canEdit(page) = canView(page) AND spaceRole ≥ editor
                AND every edit-restriction on page + ancestors passes
comment       = requires canView
```

On a **replica space** (§12), `canEdit` is unconditionally false — the
read-only invariant of one-way sync beats every grant and restriction. All
other mutations, comments included, are equally blocked there
(`ReadOnlyReplicaError`).

Moving a page re-evaluates nothing — restrictions are positional, so a page
moved under a restricted parent immediately inherits that parent's
restrictions. The move UI warns when a move would change who can see a page.

### 6.4.1 Structural operations touch more than one page

Move and delete change the tree, so a single-page permission check is not
enough — checking only the obvious page turns each into a way to act on
pages you have no rights to.

- **Move requires `canEdit` at both the source and the destination.**
  Otherwise "move" becomes a way to relocate a page across a restriction
  boundary you have no edit rights on — pushing content into a restricted
  area, or dragging it out of one.
- **Delete cascades to the whole subtree**, as one audited operation, and
  restore brings the subtree back. Soft-deleting a parent while leaving
  children live would orphan them in the tree.
- **Subtree delete requires `canEdit` on every page in the subtree.** This
  is the sharp edge: without it, deleting a parent is a way to destroy
  restricted descendants you could never have edited directly. If any
  descendant fails the check, the whole delete is refused rather than
  partially applied — and the refusal says how many pages blocked it, not
  which ones, since their titles may themselves be restricted.

### 6.4.2 Comments and labels

Neither is covered by the page rules alone, so:

- **Comments.** Reading and adding require `canView` — commenting is not
  editing. **Editing a comment is author-only**: changing someone else's
  words under their name is misattribution, not moderation. **Deleting**
  allows the author *or* anyone with `canEdit` on the page, which is the
  moderation path. Deletion is a tombstone (§5), never a row removal.
- **Labels.** Creating a label in a space requires `editor` — tagging is
  routine, not a taxonomy decision reserved to admins. Attaching or
  detaching requires `canEdit` on that specific page.
- **Label listings are permission-filtered per page.** Labels are flat, not
  hierarchical, so a listing cannot use the page tree's prune-the-subtree
  shortcut: every candidate page needs its own restriction check against its
  own ancestor chain. A restricted page is absent entirely — not shown as a
  redacted row, and not implied by a count (§6.7).
- Audit uses the existing subject types: label creation against the space,
  attach/detach against the page. No `Label` subject type — the action name
  (`label.attach`, …) carries the distinction.

### 6.5 Instance roles and no-bypass

Keycloak realm/client roles map to instance roles: `admin` (manage spaces,
attribute registry, settings) and `user`. Deliberately, **instance admins do
not bypass page restrictions** — with export-control content there must be no
silent read-around. Admins can *edit* rules (and thereby grant themselves
access), but every rule change is audited (who, when, before/after), so
widening access always leaves a trace.

### 6.5.1 Space lifecycle

- **Create** requires instance `admin`. Nothing else can authorize it: a
  space that does not exist yet has no grants to evaluate a role against.
  **Creation is atomic with its first grant** — the caller supplies the
  initial grant and both commit in one transaction, so a space never exists
  in a state where nobody can administer it. There is no default grant:
  silently opening a new space to `everyone` is exactly the footgun this
  model exists to prevent, so the initial grant is a required input.
- **Rename, archive, and restore** require instance `admin` **or** that
  space's own `space-admin`, consistent with `space-admin` meaning "manage
  this space" (§6.4).
- **Archiving does not touch pages.** An archived space is hidden from
  browse and becomes read-only; its content is untouched and restore
  reverses it. This is deliberately unlike page delete (§6.4.1): delete
  removes content and therefore needs `canEdit` on every affected page,
  whereas archive removes neither content nor anyone's access to it once
  restored, so it needs no per-page check and no cascade.

Whether editors and viewers should still *read* an archived space, or
whether archive hides it from everyone but its admins, is an open question.

### 6.5.2 Who may manage access rules — and the bootstrap

Rule management requires instance `admin` **or** that space's `space-admin`.
The instance-admin arm is not a convenience: without it the model deadlocks.

A space with zero grants confers no role on anyone, so `space-admin` is false
for every principal — meaning **nobody can ever create the first grant**.
Creation-atomic-with-first-grant (§6.5.1) prevents that state arising, and
the instance-admin arm is the recovery path if a space ever reaches it
anyway (every grant deleted, an import half-completed, a restore from a
partial backup). Without both, a space can become permanently unadministrable
with no way back that doesn't involve hand-editing the database.

This does not weaken §6.5's core rule. Instance admins still **cannot read
around page restrictions**. They can change rules — which §6.5 already
allowed — and every change is audited with full before-and-after state (§7),
so widening access is always visible. Read-around leaves no trace; rule
changes do. That distinction is the whole design.

### 6.6 Tooling (ships with the feature, not after)

Rule systems fail in the UI, not the engine:

- **Rule builder** — visual AND/OR group editor with pickers for known groups,
  registered attributes, and users. No raw JSON editing for normal admins.
- **Permission inspector** — "why can / can't user X see this page": shows the
  space role computation and each restriction with pass/fail per condition.
  Note this needs a **non-short-circuiting** evaluation path: the enforcement
  gate stops at the first failing restriction (correct and cheap), but an
  inspector that stops there can only ever show one reason, which is the
  least useful answer when several rules are in play. The explain path is a
  separate method, never a relaxation of the gate.
  *Shipped: the inspector is the root query `effectivePermission(pageId,
  subject?)` — self-inspection for anyone who can view the page; a
  principal-shaped `subject` (instance admins only) for inspecting or
  what-if-testing another principal, since no other user's token exists to
  build a real Principal from. The non-short-circuiting path is
  `EffectivePermissionCalculator.Explain`, pinned by test to agree with the
  enforcement gate's verdict and reasons exactly. The caller's own canView
  gates every mode — the inspector is not a read-around, even for admins.
  Every inspection is audited as `permission.inspect` with the inspected
  principal's id (never their attribute values).*
- **Restriction banner** — a page with active restrictions shows a lock badge
  listing the effective rules, so authors know a page is limited.
- Known groups are accumulated from observed logins (plus manual add), avoiding
  a Keycloak admin-API dependency in v1.

### 6.7 Enforcement points

Every read path enforces `canView`: page fetch, tree (filtered during the
walk, ancestor restrictions carried down the recursion), search (keyword +
vector results post-filtered, §9), attachments (auth-checked before
streaming), comments, and revision history. Rules are cached in memory (they're small and few);
evaluation is pure in-process boolean logic, so per-page checks are cheap.

**Reads return "absent", never "forbidden".** A restricted page is
indistinguishable from a nonexistent one: `null`, or simply missing from the
tree. Mutations may return a typed `Forbidden` error — the caller already
knows the page exists — but on a read path that distinction *is* the leak,
confirming a page's existence, and often its title, to someone with no right
to know. No `ForbiddenError` on reads, no placeholder rows, no gaps in
ordering that imply something was removed.

**Indistinguishable to the caller, not to the audit log.** §7 requires
denials to be recorded with the failing restriction, which the read services
know and the API layer cannot infer from a bare `null`. So read services
return an **internal** result distinguishing *not found* from *denied (with
reason)*, and the resolver collapses both to `null` at the boundary while
auditing the denial. The distinction exists throughout the system and dies
exactly once, at the response edge. A service that returns bare `null` makes
denial auditing impossible — that is the anti-pattern to avoid.

---

## 7. Audit logging

Compliance requirement: **every user action is audited** — reads and writes,
successes and denials. Retrofitting audit is miserable, so it ships as core
infrastructure in the first real milestone, not as polish.

### Event model

Append-only `AuditEvent` table:

| Field | Example |
|---|---|
| Timestamp (UTC) | `2026-08-21T03:14:07Z` |
| User | local user id (from `sub`) |
| Action | `page.view`, `page.edit`, `page.move`, `space.browse`, `attachment.download`, `search.query`, `permission.change`, `audit.view`, `sync.import`, … |
| Subject | type + id (page, space, attachment, comment, rule) + space key |
| Outcome | `success` or `denied` |
| Request context | request id, client IP, channel (`graphql` / `mcp` / `attachment`) + MCP client name |
| Details (JSON) | revision created, search query text, rule before/after, failing restriction on a denial |

The `search.query` row's Details JSON carries the raw query text, facets,
and result count — the audit table, not telemetry (§15), is where
who-searched-what lives.

### Emission and guarantees

- Implemented in the GraphQL execution pipeline: every root query/mutation
  field declares its audit action and subject, and a schema test fails the
  build if one ships without a declaration — same enforcement style as the
  Markdown round-trip rule. Nested reads are covered too: resolving a page's
  content emits `page.view` wherever it appears in a query (§8). The
  attachment routes and MCP tools carry the same declarations through their
  own pipelines — every channel lands in the same audit table.
- Mutations write their audit event **in the same transaction** as the change —
  an edit cannot exist without its audit record.
- Reads are audited synchronously too: if the audit insert fails, the request
  fails. Fail-closed applies to observability, not just access.
- Denied requests are recorded along with which restriction failed — this
  feeds the permission inspector and makes probing visible. For reads, the
  failing restriction travels from the rule engine to the audit row inside
  the read service's internal result (§6.7) and is written as
  `{"reason":"restriction:{pageId}:{ruleId}"}` — the same details shape
  mutation denials use — while the response stays identical to a not-found.
  A not-found read is not audited: `success`/`denied` is the complete
  outcome vocabulary, and no access decision exists to record for a subject
  that isn't there.
- Append-only is enforced at the database: the app's SQL login has
  INSERT/SELECT only on this table, no UPDATE or DELETE grants.
- Sign-in/out events live in Keycloak's own event log; RocketWiki audits
  application actions and correlates by user id.
- Audit is a **database table** (`AuditEvent`), not a log stream — queryable,
  partitioned, and grant-protected. Operational telemetry (§15) is a
  separate concern and deliberately carries no content or identity detail.
- **Rule changes record full before-and-after state**, not diffs. SQL Server
  temporal tables were rejected because SQLite cannot emulate them and the
  test tier must exercise the real schema (data-model.md, *Temporal tables —
  considered and rejected*). That makes the audit log the *only* record of
  rule history, so it must be complete enough to reconstruct the rule set at
  any past instant by replay. Enforced by test.

### Volume and access

- Page views dominate volume. The table is date-partitioned and an archival
  job moves old partitions to cold storage (retention period: open question).
- Audit log viewer for instance admins: filter by user, action, subject,
  outcome, and date range; CSV export. Viewing the audit log is itself
  audited (`audit.view`).

---

## 8. API design

The primary API is a single **GraphQL endpoint** (`/graphql`) served by
**Hot Chocolate**, JWT bearer auth; attachment binary and MCP are the two
other surfaces, below. Code-first schema in C#; the SDL is exported to
`schema.graphql` in the repo and a CI check fails if code and file drift —
schema evolution is always visible in review, and the frontend generates its
typed client from the same file.

Rendering happens client-side (same pipeline as the editor), so the API
serves Markdown, not HTML. One renderer means no drift between edit and view
modes.

### Schema sketch (abbreviated)

```graphql
type Query {
  spaces: [Space!]!                # only spaces the caller can view
  space(key: String!): Space
  page(id: ID!): Page
  search(query: String!, spaceKey: String, labels: [String!],
         after: String): SearchConnection!
  me: CurrentUser!
  auditEvents(filter: AuditFilter!, after: String): AuditEventConnection!  # admin
  groups: [String!]!               # known groups, for the rule builder
  attributeRegistry: [AttributeDefinition!]!
}

type Space {
  key: String!
  name: String!
  homepage: Page
  tree: [PageTreeNode!]!           # filtered during the walk (§6.7)
  grants: [SpaceGrant!]!           # space-admin only
}

type Page {
  id: ID!
  title: String!
  content: String!                 # Markdown — resolving this emits page.view (§7)
  parent: Page
  children: [Page!]!
  revisions(after: String): RevisionConnection!
  comments: [Comment!]!
  attachments: [Attachment!]!
  labels: [String!]!
  restrictions: PageRestrictions!  # own + inherited, flagged
  effectivePermission(userId: ID): EffectivePermission!   # inspector
}

type Mutation {
  createPage(input: CreatePageInput!): CreatePagePayload!
  updatePage(input: UpdatePageInput!): UpdatePagePayload!
  movePage(input: MovePageInput!): MovePagePayload!
  deletePage(input: DeletePageInput!): DeletePagePayload!   # soft delete
  restoreRevision(input: RestoreRevisionInput!): RestoreRevisionPayload!
  addComment(input: AddCommentInput!): AddCommentPayload!
  setSpaceGrants(input: SetSpaceGrantsInput!): SetSpaceGrantsPayload!
  setPageRestrictions(input: SetPageRestrictionsInput!): SetPageRestrictionsPayload!
}
```

### Conventions and guardrails

- **Mutation conventions** (Hot Chocolate's built-in pattern): every mutation
  returns a payload type with typed errors. An edit conflict is a
  `StaleRevisionError` carrying the newest revision — data for the merge UI,
  not a transport error; a write into a replica space is a
  `ReadOnlyReplicaError` (§12).
- **Cursor pagination** (connections) on revisions, search, and audit events.
- **DataLoaders** batch children, labels, and authors — no N+1 queries when
  resolving the tree or search results.
- **Object-level authorization, not root-level.** Nested traversal means a
  `Page` is reachable many ways (root query, `children`, `parent`, search
  hits, `comments`). A field middleware runs `canView` on every resolved
  `Page` regardless of path, so traversal can never leak a restricted page
  (§6.7).
- **Audit mapping (§7).** Every root field declares its audit action; a schema
  test fails the build on undeclared fields. Additionally, resolving
  `Page.content` or `Page.revisions` emits `page.view` (deduplicated per
  request) — reading content deep inside a query is still a view. Tree/title
  browsing audits as `space.browse`; `search` as `search.query`.
- **Query limits:** max execution depth and complexity caps plus paging
  limits. Persisted operations (server accepts only known queries) are a
  later hardening step once the UI's query set stabilizes.

**Known deltas between the sketch above and the exported schema** (surfaced
reconciling the SPA to `schema.graphql`): the read-path half is closed —
`Space.isReplica` (and the uncallable `isReplicaOf(localInstanceId!)`
removed), `groups`/`attributeRegistry` for the rule builder (gated to rule
managers, enumerated honestly from local data — stored rules, registered
`AttributeDefinition` rows, the caller's own token, and, for instance
admins only, the User mirror; suggestion-vocabulary, never authority),
`labelDetails` (Query + Page) giving labels ids, `archivedSpaces` (scoped
exactly to who `restoreSpace` accepts), `UserRef` display-name resolution
for comment/attachment/trash authors, viewer watch state, tree-node labels,
and audit `totalCount`. New §7 actions: `permission.vocabulary` (success +
audited denial) and `permission.inspect` (every inspector call, success and
denial, self and foreign); watch-state and tree-label nested fields
deliberately emit no audit rows of their own. The permission half is closed
too: `Page.canEdit`/`canComment`/`canManageAccess` (batched — 4 queries per
page batch — and replica-aware), the manage-gated `Page.restrictions`
listing (own + inherited flagged), the §6.6 inspector (as the root query
`effectivePermission` rather than the sketch's `Page.effectivePermission`
field — a deliberate divergence so the non-admin refusal happens before any
page lookup), tree restriction markers
(`PageTreeNode.hasRestrictions`/`ownViewRestrictions`, a list where the
SPA's placeholder guessed a singular) for §6.4's move warning, and
`CurrentUser.isInstanceAdmin`/`localUserId`. Remaining SPA-side wiring of
these shapes (enum casing, expressionJson parsing, the singular→list
adaptation) is the un-stub round's work — the `NOTE (schema
reconciliation)` markers come out as it lands.

### Non-GraphQL routes: attachment binary

```
POST /attachments/{pageId}   multipart upload
GET  /attachments/{id}       download — auth-checked, audited, then streamed
```

Streaming large binaries doesn't belong inside a GraphQL response. These two
routes run through the exact same authorization and audit pipeline (§10).

### MCP server

A **Model Context Protocol** endpoint (`/mcp`, streamable HTTP via the
official MCP C# SDK, hosted in the same API process) lets AI assistants and
agents use the wiki as a tool — search, read pages, browse trees — **as the
user, never as a service account**:

- **Same identity, same rules.** MCP auth is OAuth 2.1, and Keycloak is
  already the authorization server. Every MCP session carries a real user's
  token with the same groups/attributes claims, builds the same principal,
  and runs the same rule engine (§6). An assistant can retrieve exactly what
  its user could open in the browser — no standing broad-access credential
  exists anywhere.
- **Same code path.** MCP tool handlers call the identical application
  services as the GraphQL resolvers. There is no parallel, subtly-different
  read path to keep honest.
- **Same audit.** Every tool call emits the standard events (`page.view`,
  `search.query`, …) with the channel marked `mcp` and the client identified
  (§7) — agent reads get the same audit coverage as human reads.

v1 tools are **read-only**: `search` (hybrid, permission-filtered — §9),
`get_page` (Markdown), `list_spaces`, `get_page_tree`. Write tools are held
back until there's a concrete, reviewed need — an agent editing controlled
documentation deserves its own design pass.

This is also the sanctioned path for "ask the wiki": the user's own
assistant does the generation while RocketWiki does permission-aware
retrieval under the user's token — no separate RAG service holding
privileged access.

### Real-time: SignalR

A **SignalR hub** (`/hubs/notifications`) pushes live notifications: a page
you watch changed, someone replied to your comment, you were mentioned, a
sync bundle landed (admins). Same JWT bearer identity as every other
channel.

**Why SignalR rather than GraphQL subscriptions** (which Hot Chocolate
supports natively): the same hub is the intended transport for CRDT
co-editing later, and GraphQL subscriptions are a poor fit for
high-frequency binary document updates. One authenticated real-time
transport beats two. If co-editing is ever dropped, Hot Chocolate
subscriptions would be the simpler choice for notifications alone.

#### Notifications must not leak

A notification reveals that a page exists, its title, and who touched it —
so it is subject to the same rules as reading (§6):

- **Per-user fan-out, never page-scoped broadcast groups.** The server
  computes candidate recipients (watchers + mentioned users), evaluates
  `canView` **per recipient at send time**, and delivers only to
  `user:{id}` groups that pass. Subscribe-time checks are not enough:
  restrictions change, and a page-scoped group would keep delivering to
  someone who lost access. That send-time evaluation requires a live
  token-built Principal, which only currently-connected recipients have. A
  candidate recipient with no open connection gets a **deferred row**
  instead: persisted with no title snapshot — no principal existed, so
  nothing is disclosed at send time — and gated exactly like a
  `sync_bundle_landed` row, at the notifications fetch, where the
  recipient's live token-built Principal must pass `canView` for the row to
  be returned at all. Its existence, not just its title, is gated, fail
  closed; a surviving row carries the page's live title, which the
  recipient could open the page to read anyway. There is no live push for
  these; the next fetch is the delivery. `markNotificationRead` runs the
  same gate, so probing row ids returns the same not-found as a row that
  never existed.
- **Minimal payloads.** Type, page id, space key, actor display name,
  timestamp — and the page title only for recipients who passed `canView`.
  Never content, never diffs.
- **Mentions** are parsed from `user://{id}` links on save (§4). Mention
  notifications are **delta-based on edit**: a page save notifies only users
  mentioned in the new revision who were not mentioned in the previous one,
  so re-saving a page never re-pings its standing mentions. Comment mentions
  notify on add. A user who is both a watcher and newly mentioned receives a
  single notification of type `mention`.
- **Replies.** A comment with a parent notifies the parent comment's author
  (`comment_reply`), through the same per-recipient canView fan-out as every
  notification and never the actor themselves. A parent author who is also
  newly mentioned by the reply receives a single notification of type
  `mention` — one notification per user per event, the most specific type
  wins, the same precedence rule as mention-beats-watch. Editing a comment
  re-scans mentions delta-style, exactly like a page edit: only users
  mentioned in the saved body who were absent from the pre-edit body are
  notified, so touching up a comment never re-pings its standing mentions.
  Comment activity does not notify page or space watchers:
  `page_watched_changed` means the page's content changed.
- **Watching is canView-gated; unwatching is not.** Creating a watch
  requires the target's own read gate (canView for a page, any space role
  for a space) — you can't watch what you can't see. Removing a watch
  touches only the caller's own row and deliberately requires no
  permission: a user who lost access must still be able to unwatch, and the
  subscription is already inert either way because canView is re-evaluated
  per recipient at send time. Watches on replica spaces are allowed: a
  Watch row is instance-local user metadata, not a write into the replica's
  synced content, and watching a replica is exactly how a user hears that a
  sync bundle changed it. When a sync bundle lands, the import writes one
  persisted `sync_bundle_landed` notification per watcher of an affected
  replica page or space per bundle — never per event. Import runs in the
  offline sync CLI, where no recipient has a live token, so unlike
  dispatcher notifications no canView can run at send time; the row
  therefore carries no title snapshot, and the check is deferred to the
  notifications fetch, where the recipient's live token-built Principal
  must pass canView (page rows) or hold any space role (space rows) for
  the row to be returned at all — the row's existence, not just its title,
  is gated, fail closed. There is no live push for these; the persisted
  table is the record and the next fetch is the delivery.
- Audit actions: `watch.add` / `watch.remove` (subject = the watched page
  or space) and `notification.markRead` flow through the domain-event
  pipeline like every mutation; the persisted list read audits as
  `notification.list`. Delivery itself remains unaudited, as above.
- Notifications are also **persisted** (`Notification` table) so users who
  were offline catch up; SignalR delivers the live nudge, the table is the
  record.
- **Delivery is not a content read**, so it emits no `page.view` (that would
  drown the audit log in machine-generated rows). Opening the page from a
  notification audits normally. Watch changes are audited as user actions.
- Multiple API instances need a **backplane** (Redis). Not required at one
  replica — but k3s makes scale-out easy, so this becomes a real decision
  at deployment time rather than a theoretical one (§15).

#### Presence: who's here, pointers, carets

Live presence on a page — avatars of everyone viewing, their mouse pointers,
and their text carets — over the same hub. It divides into two tiers that
cost very different amounts:

| Tier | Needs | When |
|---|---|---|
| **Viewer presence** — avatars, "3 people viewing", who is editing | nothing beyond the hub | v1 |
| **Mouse pointers** — live cursors with name labels | viewport-relative coordinates only | v1 |
| **Text carets and selections** — caret inside the document, remote selection highlights | **a shared document state, i.e. CRDT** | with co-editing |

The first two are just ephemeral state broadcast between clients. A **text
caret is different**: a position like "offset 412" only means something if
every client holds an identical document, and under optimistic concurrency
(§5) two editors diverge the moment one of them types — a remote caret would
drift to nonsense within seconds. Carets are therefore a feature *of* the
CRDT layer, not a separate one. See the decision note below.

**How presence works:**

- **Ephemeral, never persisted.** Awareness state lives in hub memory keyed
  by connection and dies with it; no table, no audit rows. The page view
  itself is already audited (§7) — presence adds no new record.
- **Page-scoped groups here, unlike notifications.** Presence is
  high-frequency and identical for all recipients, so per-user fan-out at
  20 Hz doesn't scale. Authorization moves to **join time** (`canView`),
  with connections **evicted and re-authorized when rules change** — the
  same cache-invalidation signal that refreshes the rule engine (§6.7)
  kicks affected presence groups. Durable, low-frequency notifications keep
  per-user fan-out; ephemeral, high-frequency presence uses groups. The
  difference is deliberate.
- **Throttled and batched.** Pointer movement is sampled client-side
  (~20/sec) and coalesced server-side; the hub uses the MessagePack
  protocol to keep frames small — which the Yjs binary updates will want
  anyway.
- **Leaving a page is a route change, not an unmount.** A single-page router
  reuses the same component instance across `/pages/:id` navigations, so
  presence subscriptions must key on the page id rather than on mount. An
  effect that only cleans up on unmount joins the first page and never
  leaves it — the user then keeps receiving presence for a page they walked
  away from, which is a live data leak rather than a stale UI.
- **Payloads carry display name and colour only.** Never attributes
  (nationality is sensitive, §6.2), never content.
- Presence is visible only to people who can already see the page, so it
  reveals nothing they couldn't learn by opening it. Whether "who is reading
  what, live" is itself acceptable in a controlled environment is a question
  for compliance, not an engineering default — flagged in open questions.

#### CRDT co-editing (v2, not v1)

The hub makes real-time co-editing reachable: TipTap already sits on
ProseMirror, so `y-prosemirror` + a Yjs provider written over the SignalR
client would give shared editing on the transport we already authenticate.
Two honest complications before committing:

1. **Server-side document state.** The simple path is a *relay* — the hub
   broadcasts opaque Yjs updates and persists the encoded state without
   interpreting it. But then the server cannot serialize the document to
   Markdown, so a designated client has to author the save. The alternative
   is materializing the doc server-side (Ycs, a C# Yjs port) so the server
   writes revisions authoritatively — more capable, less mature.
2. **Co-editing changes what "an edit" is.** Revisions are currently
   single-author (`PageRevision.AuthorUserId`); a co-editing session
   produces one debounced revision with *several* authors, and audit needs
   session semantics rather than per-keystroke events. Both the data model
   and §7 would need revisiting.

Replica spaces stay read-only — no co-editing there, ever (§12). Presence
and pointers still work on a replica; there's just nothing to merge.

**Decision needed:** live text carets are wanted (see presence above), and
they cannot ship without this layer. Either CRDT co-editing is promoted from
v2 spike to a committed milestone — which means answering the multi-author
revision and session-audit questions — or carets are deferred and v1 ships
viewer presence plus mouse pointers, which stand on their own. Recorded in
open questions.

---

## 9. Search and AI

Two retrieval modes fused into one search experience: keyword (Full-Text
Search) and semantic (embeddings + vector search). Both live in SQL Server —
no separate search or vector service to operate.

### 9.1 Keyword: Full-Text Search

SQL Server FTS over `Page.CurrentContent` + `Page.Title`, with label and
space facets. Stemming and ranking out of the box.

Milestone-4 keyword search attributes each hit to the section containing
the first literal term match: the server extracts ATX headings (fence-aware,
inline markup reduced to rendered text), computes the breadcrumb path and
anchor id with the ported cross-language algorithm (§9's shared corpus test
enforces byte-identical anchors), and builds a plain-text, match-centered
snippet — strictly after canView passes, so restricted content never reaches
the excerpting code. Hits an FTS stem matched but a literal scan cannot
locate degrade to a leading excerpt with no section attribution.
`totalCount` is the permission-filtered count, saturating at a server-side
cap (100): an exact total would evaluate canView over every candidate for a
number nobody scrolls to, and any cheaper count would leak restricted pages
into it.

### 9.2 Embedding pipeline

An **OpenAI-compatible endpoint** (self-hosted or gateway) serves an
embedding model, called through `Microsoft.Extensions.AI`'s
`IEmbeddingGenerator` abstraction so the concrete provider is pure config:

```json
"Ai": {
  "BaseUrl": "http://llm-gateway:8000/v1",
  "ApiKey": "…",
  "EmbeddingModel": "…",     // whatever the gateway serves
  "Dimensions": 1536
}
```

- Pages are chunked on Markdown heading boundaries (~500–1000 tokens, small
  overlap); each chunk embeds separately so long pages don't dilute, and
  results deep-link to the matching section via its heading path. **Deep
  links require stable heading anchors in rendered output** — without ids on
  headings the breadcrumb can name the section but not scroll to it.
- **The anchor algorithm is a cross-language contract.** The server computes
  the anchor for a search hit (only it sees the whole page, so only it can
  disambiguate repeated headings); the client renders the matching id. Two
  independently written slugifiers will agree on ordinary headings and
  diverge on the edge cases — punctuation, unicode, duplicate paths — so
  every deep link breaks silently for exactly the pages people complain
  about. Both sides therefore test against a **shared fixture corpus**
  (heading structure → expected anchor id), the same pattern as the
  Confluence converter corpus in §13. Porting the algorithm is required;
  reimplementing it is not.
- Embedding runs as a **background job** fed by the domain-event pipeline
  (page saved → re-embed), never blocking saves. Chunks are content-hashed so
  only changed chunks re-embed. Failures retry; while the endpoint is down,
  search degrades gracefully to FTS-only.
- Embedding jobs are system actions, not user actions — they don't emit user
  audit events. User-facing search stays audited as `search.query` (§7).

### 9.3 Vector storage and hybrid retrieval

- `PageEmbedding` rows: page id, chunk index, heading path, content hash,
  and the vector in SQL Server 2025's native **`vector`** column type, ANN
  index (DiskANN) + `VECTOR_DISTANCE` for cosine similarity. EF Core maps it
  via `SqlVector<float>`. Model name + dimensions are stamped per row — an
  index only ever contains one model's vectors.
- Hybrid query: over-fetch top-K from FTS and vector search, fuse with
  **reciprocal rank fusion**, then filter by `canView` (§6.7) and return the
  top N. Over-fetching before the permission filter keeps restricted-heavy
  result sets from coming back empty.
- Behind `ISearchService` like everything provider-specific: SQLite
  integration tests use the LIKE fallback plus in-memory cosine similarity;
  the real FTS + DiskANN path is covered by the SQL Server Testcontainers
  suite (§14).

### 9.4 Instances and compliance

- **Embeddings are derived data — they never sync.** Each instance (low and
  high) embeds its own content, including replicas on import, against its own
  endpoint (§12). This keeps the high side self-contained and lets each side
  run whatever model its network can host.
- Page content is sent to the embedding endpoint, so **the endpoint is
  inside the security boundary** — self-hosted or an approved gateway on the
  same network, never a public API. This is a deployment requirement, not a
  suggestion.

This index also powers "ask the wiki" through MCP (§8): the user's own
assistant does the generation over permission-aware retrieval. Whether a
built-in assistant UI is wanted on top remains an open question.

---

## 10. File storage

Attachment bytes live in **S3-compatible object storage** (MinIO, Ceph, R2,
AWS S3 — anything speaking the S3 API), behind a small abstraction so local
dev and tests run against the plain filesystem with no infrastructure.

### The abstraction

```csharp
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
}
```

Two providers, selected by configuration:

- **`S3FileStorage`** — `AWSSDK.S3` with a configurable `ServiceURL` and
  path-style addressing, so any S3-compatible endpoint works, not just AWS.
- **`FileSystemFileStorage`** — a directory on disk. Used by local dev and
  integration tests; also fine for the simplest single-box deployments.

Storage keys are opaque (`attachments/{yyyy}/{MM}/{guid}`) — page moves and
renames never touch storage; the `Attachment` row owns all meaning.

### Rules that interact with access control (§6) and audit (§7)

- **No presigned URLs.** Downloads always stream through the API: a presigned
  URL would bypass both page restrictions (`canView`) and the audit log. The
  perf cost is acceptable at wiki scale.
- **Consequence for image rendering:** an `<img src>` cannot carry an
  `Authorization` header, so inline images can't point at the API directly.
  The client fetches the bytes through the authenticated API and renders them
  from a `blob:` object URL — auth happens at fetch time, and no
  unauthenticated URL for the object ever exists. Object URLs must be revoked
  on unmount, or decoded image memory leaks for the life of the tab.
- **Upload order:** write bytes to storage first, then commit the `Attachment`
  row + audit event in one DB transaction. A nightly janitor deletes storage
  objects with no matching row (failed uploads); a row whose object is
  missing surfaces as a flagged error, not a 500.
- **Size limit:** `Attachments:MaxSizeBytes` (default 100 MiB, matching the
  nginx `client_max_body_size` in front of the API — the proxy line now
  mirrors the API's limit instead of defining the system's only cap) is
  enforced in the upload route before any blob write or row insert,
  returning a structured 413. A refused-too-big upload is not audited: like
  a validation failure, it involves no access decision (§7's outcome
  vocabulary).

```json
"FileStorage": {
  "Provider": "S3",                          // or "FileSystem"
  "S3": { "ServiceUrl": "http://minio:9000",
          "Bucket": "rocketwiki", "ForcePathStyle": true },
  "FileSystem": { "Root": "/data/attachments" }
}
```

---

## 11. Authentication flow

1. SPA uses `react-oidc-context` (oidc-client-ts) for Authorization Code + PKCE
   against Keycloak; tokens kept in memory, silent renew via refresh token.
2. API validates bearer JWTs against Keycloak's JWKS
   (`AddJwtBearer` with authority = realm URL).
3. On each authenticated request, middleware upserts the local `User` row from
   claims (JIT provisioning), including registered attributes (§6.2).
4. The request principal (groups + attributes) is built from the token and
   handed to the access-rule evaluator (§6) — the local mirror is never used
   for authorization decisions.
5. Keycloak setup required: a `groups` protocol mapper and one mapper per
   registered attribute (e.g. `nationality`) on the RocketWiki client, so the
   claims actually appear in access tokens.

MCP clients (§8) authenticate against the same Keycloak realm via OAuth 2.1
and hit the same bearer-token validation — one auth path for every channel.

If token-in-browser is ever deemed unacceptable, the fallback is a BFF with
cookie auth — noted as an option, not planned.

---

## 12. Multi-instance sync (low → high)

RocketWiki runs as **multiple independent instances on separated networks** —
"low" and "high" — with content flowing **one way only** (low → high) across a
controlled boundary. The transport itself (data diode, guarded transfer,
manual media) is out of scope; the design produces and consumes **bundle
files** and never assumes a back-channel.

### Model

- Every instance has an `InstanceId`. Every space records its
  `OriginInstanceId`: a space is **native** (authored here) or a **replica**
  (imported from a lower instance).
- **Spaces are the sync unit.** On low, a space is flagged *exported*; on
  high it materializes as a replica.
- **Replicas are read-only. Always.** On a replica, `canEdit` is
  unconditionally false — an instance-level invariant in the rule engine that
  beats every grant (§6.4). The UI shows a "mirrored from LOW — read-only"
  banner; mutations fail with a typed `ReadOnlyReplicaError` (§8).
- One-way flow + immutable replicas = **no merge problem by construction**.
  Serialized replay cannot conflict, which is what makes one-way sync safe.

### Change journal (outbox)

All mutations already flow through a domain-event pipeline (it feeds audit,
§7). On low, events touching exported spaces are additionally appended to a
**sync outbox**: an append-only journal with a strictly monotonic sequence
number per exported space. Event types: page created/updated (full Markdown,
not a diff), moved, deleted/restored, comment added, attachment added
(referenced by content hash), labels changed, restrictions changed.

### Bundles

An export job drains the outbox into **numbered bundles**:

```
bundle-000041.zip
├── manifest.json    instance id, bundle number, per-space event ranges,
│                    SHA-256 of payload, hash of previous manifest (chain)
├── events.ndjson    ordered events
└── blobs/           attachment bytes, keyed by content hash
```

- A newly exported space first produces a **baseline bundle** (full snapshot
  including revision history), then incrementals.
- Import applies bundles **strictly in order and refuses gaps**: if bundle 41
  hasn't been applied, 42 waits. Apply is idempotent, so duplicate delivery
  is harmless.
- The manifest hash chain makes a missing, reordered, or tampered bundle a
  detected error, never a silent absorb. Every import is audited
  (`sync.import` with bundle id and event range).

**Bundle format versioning.** The manifest declares `formatVersion`; its
absence marks format 1 (the original, current-state-only baselines). Format
2 delivers the "full snapshot including revision history" above — a
baseline PageUpsert carries every revision of its page, an incremental
PageUpsert the one revision it corresponds to, each author
shadow-user-resolvable — and stores events under `events.v2.ndjson`. That
rename is deliberate: a format-1 importer would otherwise absorb a format-2
bundle while silently discarding its history; instead its
missing-`events.ndjson` guard refuses loudly (exit 2,
`sync.import.refused`). A format-2 importer accepts format-1 bundles as the
current-state snapshots they always were, and refuses any newer
`formatVersion` with a typed error before parsing a single event. Revision
data is attached at export time from the immutable PageRevisions table, so
outbox rows journaled before format 2 existed still export with full
history.

**Exported-ness is a low-side property.** Only a native space can be
exported; a replica must never emit sync events for content it doesn't own.
The outbox writer enforces this rather than assuming it: an outbox entry is
written only when `Space.IsExported` **and** `OriginInstanceId` equals the
local instance id (threaded into the DbContext via `UseLocalInstanceId` on
its options). A replica flagged exported — corrupt state, since no app path
exports a replica and import writes `IsExported = false` — journals nothing
while the local mutation itself (e.g. replica-side rule management, which is
legitimately local) still commits; a context that never declared its
instance id fails loudly the moment an exported space needs journaling, so a
misconfigured writer can never silently produce an incomplete sync stream.
Defense in depth: the one-way guarantee is the whole point of the design.

### What travels, what stays local

| Travels with content | Stays local to each instance |
|---|---|
| Pages + full revision history, tree structure, labels | Space grants — high decides who can view its replica |
| Attachments (bytes + metadata) | Audit log — each side keeps its own |
| Comments made on low | Users and logins — each side has its own Keycloak |
| Page **restrictions** (fail closed: a group/attribute unknown on high matches nobody) | Search index + embeddings — recomputed locally on import (§9.4) |
| | Space **lifecycle and identity** — name, description, archived state. Spaces aren't a sync event type: import creates the replica row from the space key alone, so renaming or archiving a replica is legitimate local curation (like grants), not a blocked content write |

- IDs are GUIDs and survive the crossing, so `page://` and `attachment://`
  links keep working on high.
- Authors arrive as **shadow users** (name/email from the event, flagged
  external, never loginable) so history and bylines render without mapping
  to high-side accounts.

### Operations

`RocketWiki.Sync` CLI with two verbs: `export` (low: drain outbox → bundle
files) and `import` (high: verify chain → apply in order). Runnable manually
or as a scheduled job. An admin **sync status** page shows the last bundle
applied, per-space sequence positions, and loud warnings on gaps or chain
breaks.

The CLI is `RocketWiki.Sync export --connection-string … --output <dir>
--instance-id <id> --attachments-root <dir> [--baseline <space-key>]` and
`RocketWiki.Sync import --connection-string … --bundle <file-or-dir>
--origin-instance-id <id> --attachments-root <dir>`. `--baseline` runs
exactly once per newly exported space and is refused for a space that isn't
flagged exported or that this instance doesn't own. Import in directory mode
applies every `bundle-*.zip` in bundle-number order; exit code 2 (vs. 1 for
usage errors) marks integrity refusals so a scheduled job can page on them.
An integrity refusal also leaves a durable audit row: `sync.import.refused`
on the sync channel, recording the bundle file name, origin instance, and
refusal reason (gap, chain break, payload-hash mismatch, per-space sequence
gap, or unreadable file). It is written on a fresh unit of work — never the
one holding the partially-applied bundle — and is deliberately not
`sync.import` with a `denied` outcome: §7's denied names a principal
refused by a failing restriction, and an integrity refusal has neither; the
refusal itself is the successfully-completed action being recorded.
The admin status data is served by the admin-only `syncStatus` GraphQL query
(audited as `sync.status`): per exported space the outbox position, pending
event count and last drained bundle; per origin instance the last bundle
applied, its manifest hash (the link the next bundle must chain from), the
import time, and per-space applied sequences. `ReadOnlyReplicaError` carries
the space id and its `OriginInstanceId`, so the client can render "mirrored
from LOW — read-only" rather than a bare refusal.

---

## 13. Confluence migration

A separate console tool (`RocketWiki.Importer`), not part of the running app:

1. Read a Confluence space export (or pull via Confluence REST API).
2. Convert storage-format XHTML → Markdown (custom converter; Pandoc as a
   starting point, plus handlers for common macros: code, info/warning panels →
   callouts, TOC → dropped, unsupported macros → flagged in an import report).
3. Recreate the page tree, upload attachments, rewrite internal links to
   `page://` form, map authors by email.
4. Emit a per-page report of anything lossy so owners can review.

Note that imported Markdown is subject to the canonical normalizations in
§4 — emphasis delimiters and repeated `1.` list markers reformat the first
time a migrated page is saved. Cosmetic, but worth saying out loud before
someone reports it as data loss.

**The real migration risk is the two systems agreeing.** A converter that
produces plausible-looking Markdown proves nothing on its own: if its output
doesn't survive the editor's round-trip (§4), every migrated page silently
reformats — or worse, loses structure — the first time it is opened and
saved. The converter is C# and the editor is TypeScript, so nothing catches
this by accident.

Therefore the converter's outputs are checked in as a **shared corpus**, and
the editor's round-trip suite runs against that corpus as well as its own
fixtures. A conversion the editor cannot round-trip is a converter bug, and
this is the only test that can find it.

It earned its place immediately: the first run found the converter emitting
CommonMark *loose* lists (a blank line before a nested sub-list), which the
editor cannot round-trip because the loose/tight distinction has no
representation in the document model (§4). The blank line is not
semantically required — nesting is recognised by indentation — so the fix is
converter-side formatting. Without the corpus, every migrated page with a
loose nested list would have silently reformatted on first save.

**A documented normalization does not excuse the converter.** §4's
normalization table describes what the editor does to *any* Markdown,
including content typed by hand or pasted from elsewhere — it is a statement
about the editor, not a licence for the importer. Migration is the one case
where we control both sides, so **converter output must already be in
canonical form**: emitting content the editor will immediately rewrite means
every migrated page gains a spurious revision the first time anyone saves
it, and page history fills with changes nobody made.

So the corpus assertion stays **byte-identical**. If a fixture doesn't
round-trip, the converter changes. An exclusion list is only justified where
canonical output is genuinely unreachable — not where it is one formatting
choice away.

**Regenerating a corpus is not the same as enforcing it.** Both corpora
rewrite themselves from their source implementation on every test run, so
they cannot drift from *current* behaviour — but nothing yet fails when a
regenerated file differs from the committed one. That last mile is a CI step
which runs the suites and fails on a dirty working tree under the fixture
directories. Until it exists, "regenerated and asserted" must not be read as
"drift is impossible".

Migration fidelity is a known risk — budget real time for it, and run trial
imports early (see §16).

### Two things the importer deliberately does not do

- **Confluence permissions are reported, never translated.** Confluence's
  space permissions and page restrictions do not map cleanly onto ABAC rule
  expressions (§6.3), and an automatic translation is guaranteed to be wrong
  in one of two directions: over-open, which leaks export-controlled content,
  or over-restricted, which looks like data loss. Both are worse than an
  explicit decision. The importer therefore takes a **required** initial
  grant — never a default, never `everyone` — and reports the Confluence
  permissions it found so an admin can re-apply them deliberately.
- **Authorship needs shadow users.** Every FK requiring an author needs a
  real `User` row, so imported content is currently attributed to the
  importing actor while each page's original Confluence author (email and
  display name) is carried through the import report rather than discarded.
  Sync already has the concept — externally-flagged, never-loginable shadow
  users (§12) — and migration needs the same mechanism. Until it exists,
  author attribution is a documented manual reconciliation step, not a
  silent loss.

---

## 14. Repository layout

```
RocketWiki/
├── design.md
├── data-model.md
├── src/
│   ├── RocketWiki.AppHost/        Aspire orchestration — the topology in C#
│   │   └── keycloak/              dev realm import (mappers + edge-case users, §11)
│   ├── RocketWiki.ServiceDefaults/ health checks, OpenTelemetry, resilience
│   ├── RocketWiki.Api/            ASP.NET Core + Hot Chocolate (GraphQL)
│   ├── RocketWiki.Core/           domain model, rule engine, service contracts
│   ├── RocketWiki.Data/           EF Core, migrations, service implementations
│   ├── RocketWiki.Storage/        IFileStorage + S3/filesystem providers
│   ├── RocketWiki.Sync/           low→high bundle export/import CLI
│   └── RocketWiki.Importer/       Confluence migration console tool
├── tests/
│   ├── RocketWiki.Core.Tests/     unit tests (rule engine, permissions)
│   ├── RocketWiki.Data.Tests/     EF model against SQLite (constraints, indexes, filters)
│   ├── RocketWiki.Storage.Tests/  filesystem provider incl. path-traversal cases
│   ├── RocketWiki.Api.Tests/      integration tests + schema-drift and audit-coverage guards
│   └── RocketWiki.Data.SqlServer.Tests/   provider-specific (Testcontainers) — not yet built
└── web/                           Vite + React + TS
    ├── src/
    │   ├── editor/                TipTap setup, markdown round-trip, custom nodes
    │   ├── pages/                 routes: space browser, page view/edit, search…
    │   ├── graphql/               operations + generated typed client (codegen)
    │   └── auth/                  oidc-client wiring
    └── vite.config.ts
```

### Testing strategy

Three tiers, matching how fakeable each dependency is:

1. **Unit tests** — pure in-process: rule-expression evaluation, Markdown
   round-trip, permission computation. No database, no storage.
2. **Integration tests** — the full GraphQL API (real operations posted to
   `/graphql`) against **EF Core on SQLite**
   (in-memory, fresh schema per fixture) and **`FileSystemFileStorage`** in a
   temp directory. Fast, no containers, runs anywhere — this is the bulk of
   the suite. Keeping these green forces the EF model and LINQ to stay
   provider-agnostic.
3. **Provider-specific tests** — a smaller CI suite against real SQL Server,
   covering what SQLite cannot emulate: Full-Text Search queries, native
   `vector` + DiskANN search, audit-table partitioning and grants, and the
   checked-in migrations themselves. `Aspire.Hosting.Testing` spins up the
   AppHost's real resources for these, so the test topology is the same
   definition as dev and prod.

Anything inherently provider-specific stays behind an interface
(`ISearchService` for FTS and vector search) with naive fallbacks for SQLite
runs — LIKE for keyword, in-memory cosine for vectors — so integration tests
never need SQL Server.

---

## 15. Deployment

**.NET Aspire** owns composition. The AppHost is the one place the system's
topology is written down, in C#, and it serves both local development and
the deployment artifacts.

### AppHost app model

```csharp
var sql   = builder.AddSqlServer("sql").WithDataVolume()
                   .AddDatabase("rocketwiki");
var minio = builder.AddContainer("minio", "minio/minio")  // S3-compatible (§10)
                   .WithDataVolume();
var kc    = builder.AddKeycloak("keycloak").WithDataVolume();   // dev only
var ai    = builder.AddConnectionString("embeddings");          // external endpoint (§9)

var api = builder.AddProject<Projects.RocketWiki_Api>("api")
                 .WithReference(sql).WithReference(minio)
                 .WithReference(kc).WithReference(ai);

builder.AddViteApp("web", "../../web").WithReference(api);
```

- **Dev:** `aspire run` starts everything — SQL Server, MinIO, a Keycloak
  seeded with a dev realm, the API, and the Vite dev server — with the
  Aspire dashboard for logs, traces, and health. New contributors get a
  working stack from a clone plus one command, which matters when the
  system has this many moving parts.
- **Config by reference, not by hand.** Connection strings and endpoints are
  injected via service discovery, so `FileStorage:S3:ServiceUrl` (§10) and
  `Ai:BaseUrl` (§9) stop being copy-pasted per environment.
- **ServiceDefaults** gives every service health-check endpoints, resilience
  handlers, and OpenTelemetry wiring for free.

### Production

Two targets, same app model. **Docker Compose** first (simplest thing that
works), **k3s** as the intended landing place.

`aspire publish` generates a plain Compose file plus config — deployable
with no Aspire tooling, no cloud dependency, and no internet access on the
target network. That property is what makes Aspire safe here: the separated
networks receive ordinary containers, not an orchestration framework they'd
have to certify.

In production, Keycloak and the embedding endpoint are **existing external
services** referenced by connection string rather than containers Aspire
runs; both must sit inside the network's security boundary (§9.4). The web
app is built to static files and served by nginx, proxying `/graphql`,
`/attachments`, `/mcp`, and `/hubs` (WebSocket upgrade) to the API.

A low/high deployment (§12) is two of these stacks, one per network, each
with its own Keycloak realm, object store, and embedding endpoint —
connected only by bundle files.

### Kubernetes (k3s)

k3s suits this system: a single binary, offline-installable, no cloud
dependency, and light enough to run a whole instance per network.

**Manifest generation is not free.** Aspire's built-in publisher targets
Docker Compose; Kubernetes output comes from community tooling (Aspir8) or
hand-authored manifests/Helm. For a compliance-reviewed deployment, treat
the AppHost as the source of truth for *topology* but expect the k3s
manifests to be **reviewed artifacts in the repo**, not blindly generated
output. Generate once, then own them.

What k3s changes, concretely:

- **Migrations must leave startup.** EF migrations run on API startup today,
  which is safe only at one replica; two pods racing the same migration is
  a corruption risk. On k3s they move to a **Job (or init container) that
  runs to completion before the Deployment rolls**.
- **SignalR needs a backplane the moment replicas > 1.** Presence groups and
  per-user notification fan-out (§8) are per-process state. Either pin to
  one replica, or add Redis and accept it as a new dependency. Sticky
  sessions alone do not fix fan-out.
- **Stateful services need real storage decisions.** k3s ships
  `local-path-provisioner`, which is node-local — fine for a single-node
  instance, wrong the moment the DB pod can reschedule. SQL Server and
  MinIO get PersistentVolumeClaims on deliberate storage, or run **outside
  the cluster** entirely. Running the database outside k3s is the
  lower-risk default; backups and restore drills matter more than
  elegance here.
- **Ingress:** k3s bundles Traefik. It fronts nginx (static web) and routes
  `/graphql`, `/attachments`, `/mcp`, and `/hubs` to the API — the last
  needs WebSocket upgrade configured explicitly.
- **Secrets** (Keycloak client secret, S3 credentials, embedding API key)
  become Kubernetes Secrets, not appsettings values baked into images.
- **Probes:** ServiceDefaults already exposes health endpoints (§15) — wire
  them to liveness/readiness so rollouts wait for a healthy API.
- **Air-gapped install:** k3s supports offline bootstrap from an images
  tarball (`k3s ctr images import`) as well as a private registry mirror.
  That gives the high network a path that needs no outbound access at all.

Scaling out is optional, not implied: a single replica with the database
outside the cluster is a legitimate production shape for a wiki, and it
keeps the backplane and migration-job complexity off the table until real
load justifies them.

The milestone 9 artifacts now exist in-repo: `Dockerfile.api`/`Dockerfile.web`
at the root and a hand-authored Helm chart under `deploy/helm/rocketwiki/` —
owned manifests, not generated output, per the rule above. The shape they
encode: single api replica (enforced by a values-schema and a template guard
until a Redis backplane exists), migrations moved out of startup into a
pre-install/pre-upgrade Job running an EF migrations bundle baked into the
api image, a standard Ingress for Traefik (IngressRoute's apiVersion churn
across k3s-bundled Traefik versions makes the stable API the better reviewed
artifact; Traefik upgrades `/hubs` WebSockets natively, and the web pod's
nginx sets the upgrade headers explicitly on its own proxy hop), secrets by
reference to one operator-created Secret, and both offline image paths
(registry mirror via one values key, or `k3s ctr images import` with
`pullPolicy: Never`). One honest gap: the health endpoints this section says
to wire to probes are mapped only in the Development environment
(ServiceDefaults), so the chart defaults to TCP probes and carries an `http`
mode to flip on once `MapDefaultEndpoints` learns a config gate — a small
src change deliberately not smuggled in with deployment work. `helm lint` /
`helm template` pass; nothing has been applied to a cluster (see §16's
standing caveat).

### Telemetry is not audit

Aspire brings OpenTelemetry, which is operational data: latencies, error
rates, dependency health. It is **not** the audit log (§7) and must never
become a second, unregulated record of who read what. Traces and logs carry
no page content, no search query text, and no attribute values; page and
space identifiers only where needed to diagnose. In production the
dashboard and any collector stay inside the network boundary, same rule as
everything else.

That rule is enforced by a test, not by intent. `TelemetryHygieneTests`
(RocketWiki.Api.Tests, §14's SQLite tier) drives real requests through the
real pipeline with sentinel strings planted as a nationality value, a page
title, page content, a search-shaped inline literal, and attachment bytes;
listens to **every** ActivitySource in the process and every RocketWiki
meter; and fails if any sentinel reaches a span name, tag, event, baggage
entry, or metric tag. The listener is deliberately unfiltered, so a package
upgrade that turns on a new span carrying the GraphQL document fails the
build rather than shipping. Logs are the one gap: .NET logging isn't
interceptable the same way, so "no content in log messages" stays a review
rule.

The browser is inside that boundary too. The SPA carries the same
OpenTelemetry story — document load, fetch and XHR, and a span per GraphQL
operation carrying the operation *name* — exported over OTLP/HTTP to a
collector that stays in-network, with W3C trace context propagated **only**
to the API's own origin so a browser span joins its server span and nothing
else learns a correlatable id. Two things make this harder in a browser than
on the server, and both are handled at the exporter rather than trusted to
each instrumentation's configuration: the SPA puts search text in `?q=` and
heading text in `#anchor`, so **every URL leaving the browser has its query
string and fragment stripped**, leaving the path — and therefore the page
and space identifiers this section permits — intact. Browser telemetry is
**off unless an OTLP endpoint is configured**. There is deliberately no
default endpoint and no same-origin fallback: "someone forgot to configure
it" must fail closed rather than guess at a destination that might sit
outside the boundary.

The diagram editor URL (`VITE_DRAWIO_URL`) follows the same fail-closed
rule as the OTLP endpoint: no default exists, because opening the editor
posts diagram content into whatever page that URL serves. Unset means no
external editor loads (viewing is unaffected — diagrams are inline page
content); production points at a self-hosted in-network diagrams.net
instance (the AppHost defines a dev `drawio` container,
`jgraph/drawio:31.3.2`); the public service is a dev-only opt-in the UI
visibly flags.

#### What is instrumented

| Layer | Source / meter | What it adds |
|---|---|---|
| HTTP, HttpClient, runtime | *(Aspire ServiceDefaults baseline)* | request duration, dependency calls, GC/thread pool |
| GraphQL | `HotChocolate.Diagnostics` | operation-level spans: parse, validate, compile, execute, plus DataLoader batch spans with batch size |
| SQL | `OpenTelemetry.Instrumentation.SqlClient` | one client span per query, registered by Aspire's `AddSqlServerDbContext` (not by us — registering it again would double every span) |
| EF Core | `Microsoft.EntityFrameworkCore` meter | active DbContexts, queries, SaveChanges, compiled-query cache. Aspire's client integration registers SQL *tracing* only and no metrics at all; this is the gap it leaves |
| SignalR | `Microsoft.AspNetCore.SignalR.Server`, `Microsoft.AspNetCore.Http.Connections` meter | one span per hub method invocation (hub spans are parentless, so without this a `JoinPage` leaves no trace), plus connection counts and duration |
| Auth | `Microsoft.AspNetCore.Authorization` / `.Authentication` meters | authorization attempts by result, authentication duration by scheme. Directly relevant to a fail-closed system |
| Rule engine (§6) | `RocketWiki.Core` | rule evaluations by kind and decision; effective-permission checks by canView/canEdit and denial *category*; duration histogram |
| Domain events + audit (§7, §12) | `RocketWiki.Core` | events raised by type; audit rows written by action, outcome, channel, and which writer produced them |
| Persistence (§6.4.1, §12) | `RocketWiki.Data` | spans for units of work spanning several queries — subtree delete/restore, move, revision restore, bundle export/import — plus outbox entries appended by event type |
| Blob storage (§10) | `RocketWiki.Storage` | span, duration, count and byte count per operation, tagged by provider. Nothing else instruments this path |
| Identity + real-time (§8, §11.3) | `RocketWiki.Api` | JIT provisioning created-vs-refreshed; presence joins/leaves/evictions; notification fan-out by disposition: delivered live, deferred for offline recipients, skipped not-viewable |
| Migration (§13) | `RocketWiki.Importer` | a span per pipeline pass with page and attachment counts. No exporter is wired into the CLI |
| MCP (§8) | `RocketWiki.Api` | a span, counter and duration histogram per tool call, tagged by tool name and outcome only (bounded; unknown client-supplied names collapse to a constant). The SDK's own `Experimental.ModelContextProtocol` source is deliberately not subscribed: it records error *messages* into span status, which §15's "errors by type, never by message" rule excludes — a hygiene test sweeps it anyway, including status descriptions |
| Browser (`web/`) | `rocketwiki-web` over OTLP/HTTP | document load, fetch/XHR (covers urql's GraphQL POSTs and SignalR's negotiate/long-poll), GraphQL operation-name spans; every URL's query string and fragment stripped at the export choke point (see above) |

#### Naming

Every project declares its `ActivitySource` and `Meter` under its own
assembly name, so ServiceDefaults subscribes with a single `RocketWiki.*`
wildcard — it has to, since every project references ServiceDefaults and a
reference back would be circular. That makes the convention load-bearing: a
source named outside it would compile, emit, and be silently dropped, so a
test asserts every telemetry class matches the pattern. Instruments are
`rocketwiki.<area>.<thing>` and tags `rocketwiki.<area>.<tag>`, following
OpenTelemetry's lowercase dotted convention.

#### Rules for adding instrumentation

- **Tag vocabularies are bounded.** Enum names, decision outcomes, and fixed
  reason categories — never free text. A failed restriction's audit reason is
  `restriction:{pageId}:{ruleId}`; as a metric dimension that becomes one
  series per rule, so it collapses to `restriction`. The audit log keeps the
  specific reason, which is where it belongs.
- **Errors are recorded by type, never by message.** `StaleRevisionError`
  carries the page's latest title and content; a message tag would leak both.
  GraphQL error *events* are suppressed entirely (`MaxErrorEvents = 0`) for
  the same reason — `graphql.error.message` echoes the client's own query
  text back. `graphql.error.count` and `error.type` survive, which is the
  "error rates" this section asks telemetry to provide.
- **Identifiers go on spans, not on metric dimensions.** A Guid tag on a
  counter is both a cardinality problem and wider exposure than a dashboard
  needs.
- **Counters increment on commit.** The domain-event pipeline builds the
  mutation, its audit row and its outbox entry in one change set; counting
  before `SaveChanges` returns would report writes a rolled-back transaction
  never made.
- **GraphQL request details are opt-in, never inherited.** Document text and
  variables are excluded, and so are `extensions` — which the library's own
  default *does* include, written verbatim as a client-controlled blob.
  Document id, hash and operation name remain: enough to find a slow query
  without seeing what it asked for.
- **Resolver-level spans stay off.** `canView` runs on every resolved Page
  (§6.7), so per-field spans would be hundreds per page-tree query and would
  only restate the operation-level view. DataLoader batch spans are kept —
  they are the only direct measurement of whether batching actually batches.

---

## 16. Milestones

Status is tracked here as work lands. **"Done" means covered by passing
tests**, not that it has run against real infrastructure — see the container
caveat below the table.

| # | Milestone | Status | Contents |
|---|---|---|---|
| 0 | Walking skeleton | **done** (bar `aspire run`) | Aspire AppHost + ServiceDefaults, Vite app scaffolded, Keycloak dev realm with the §11 protocol mappers, schema-drift + audit-coverage guards |
| 1 | Editor spike ⚠️ | **done** | TipTap + Markdown round-trip for the full v1 feature set, proven against a real editor instance. Was the highest-risk item; it held. |
| 2 | Core wiki | **done** | Rule engine, EF model proven on SQLite, domain-event pipeline (audit in the same transaction), page CRUD + subtree delete, permission-filtered reads (incl. §6.7's not-found-vs-denied result with denied-read auditing), GraphQL resolvers + object-level authorization (adversarially tested), access-rule management with replay-provable history, space CRUD |
| 3 | Content features | **done** | Attachments (S3 + filesystem providers; S3 unverified against a live endpoint), comments, labels — all wired end to end and audited |
| 4 | Search & polish | **done** (bar real-FTS verification) | `search`/`labels` API matching the shipped UI operations, permission-filtered with section attribution; SQL Server FTS path TODO-flagged until the container tier exists (SQLite LIKE fallback is what tests exercise); trash/restore, space management UI, rule builder + permission inspector, audit log viewer, import report UI |
| 4b | Notifications & presence | **done** (live hub unexercised) | SignalR hub, watches, delta-based mentions and reply notifications, per-recipient `canView` fan-out (re-checked at read time too), persisted notification list incl. `sync_bundle_landed` rows from the offline import. The SPA now generates its client from the exported `schema.graphql` (placeholder deleted), runs the real SignalR transports by default (fakes only behind `VITE_FAKE_REALTIME`, for tests and backend-less dev), and wires the bell (persisted list + live push, de-duplicated by row id), watch/unwatch on pages and spaces, and the §12 admin sync status page. Per the standing caveat no browser has ever actually connected to the hub |
| 5 | Migration | not started | Importer against a real Confluence space export; trial runs and fidelity review |
| 6 | Low/high sync | **done** (baselines are current-state-only) | Outbox journal, `RocketWiki.Sync` export/import CLI with hash chain, baseline snapshots (documented simplification: no revision history), replica read-only enforcement with `originInstanceId` in the error, admin `syncStatus` query |
| 7 | Semantic search | **done** (fake endpoint; exact-scan vectors) | Heading-boundary chunker over the shared anchor primitives; `PageEmbeddingState`-driven polling background job (covers sync-CLI writes; per-chunk hash re-embed; failure backoff; trash purge); `IEmbeddingGenerator` via Microsoft.Extensions.AI.OpenAI from the Aspire `embeddings` connection string — unconfigured means keyword-only, structurally; hybrid RRF inside the same `search` field (no schema change), canView after fusion, semantic hits deep-link via chunk attribution recomputed post-canView; `rocketwiki.embeddings.*` telemetry with a sentinel hygiene test. Native `vector` + DiskANN remain TODO-flagged (the conversion is deliberately not shipped ahead of the container tier — see the AddPageEmbeddingState migration); the exact-scan cosine fallback runs on both providers until then |
| 8 | MCP server | **done** (no live Keycloak/OAuth dance yet) | `/mcp` (streamable HTTP, stateless, in-process) via the official C# SDK; RFC 9728 resource-metadata discovery pointing at Keycloak; four read-only tools over the shared service layer; per-call `mcp`-channel audit incl. client name and denied-read reasons; audit-declaration guard extended to tools; `rocketwiki.mcp.*` telemetry |
| 9 | k3s deployment | **authored, unexercised** | Dockerfiles + Helm chart in-repo (`deploy/`), migration Job via EF bundle, Traefik ingress with WebSocket upgrade, secrets by reference, probes (TCP until health endpoints get a non-Dev config gate), offline image path. `helm lint`/`template` pass; nothing applied to a cluster; restore drill unrun |

### The standing caveat

Everything above is verified by **tests**, not by running. No container has
ever started in development: no `aspire run`, no migration applied to real
SQL Server, no Keycloak realm imported, no token decoded to confirm the
`groups` and `nationality` claims actually arrive, no S3 call against a live
bucket. Full-text search, the `vector` type, DiskANN, audit partitioning and
the append-only grants are all SQL Server features the SQLite tier cannot
exercise — they remain TODO-flagged in the migration and unproven.

That gap closes the day a container runtime is installed, and closing it is
the highest-value unblocking action available.

---

## 17. Open questions

- [ ] Expected scale? (users, pages — affects whether SQL Server FTS is enough)
- [ ] Page URLs: `/{spaceKey}/{page-slug}` (pretty, needs redirect handling on
      rename) vs id-based `/pages/{id}/{slug}` (stable). Leaning id-based.
- [ ] Retention: keep every revision forever, or compact old history?
- [ ] Do we need page templates (Confluence-style) in v1 or later?
- [ ] Archived spaces (§6.5.1): read-only-but-visible to their existing
      viewers, or hidden from everyone except admins?
- [x] Diagramming — resolved: both. Mermaid fences render client-side
      (mermaid bundled locally, lazy-loaded, `securityLevel: strict`), and
      draw.io diagrams embed inline as base64 editable SVG in a ` ```drawio `
      fence, edited via the diagrams.net iframe embed protocol against a
      self-hosted instance. No schema/API changes; diagrams are ordinary
      page Markdown to everything but the SPA.
- [ ] Which attributes beyond nationality? (clearance level, employer/contractor
      status?) Each needs a Keycloak attribute + protocol mapper.
- [ ] Audit retention: how long must events be kept, and where do archived
      partitions go (cheap SQL table, object storage, SIEM)? Note audit is
      now the sole record of access-rule history (§7), so its retention
      period is a compliance question, not just a storage one.
- [ ] Dual nationals: confirm nationality is `string[]` in Keycloak and that an
      `in` condition matches on *any* held nationality — or must it match a
      stricter policy?
- [ ] Sync transport: what actually carries bundles across the boundary
      (diode product, guarded file transfer, manual media), and at what
      cadence?
- [ ] Do group/attribute names align between the low and high Keycloak realms?
      Synced page restrictions fail closed on high if not.
- [ ] Do high users need local-only comments/annotations on replica pages, or
      is fully read-only acceptable for v1?
- [ ] Is it one low → one high, or can one high import from several lows?
      (The per-origin bundle streams support it; needs a decision.)
- [ ] Which embedding model does each instance's endpoint serve, and does the
      high network have its own model server? (Dimensions are fixed per
      index; changing model means re-embedding everything.)
- [ ] Does each network have a container registry mirror for the published
      images (SQL Server, MinIO, nginx, api/web), or must deployment ship
      image tarballs? (k3s supports both — see §15.)
- [ ] k3s: does SQL Server run inside the cluster on a PVC, or outside it?
      (Outside is the lower-risk default; inside needs a deliberate storage
      class and a tested restore path.)
- [ ] k3s: one API replica (simplest — no backplane, no migration Job) or
      scale-out from the start? This decides whether Redis becomes a
      dependency.
- [ ] "Ask the wiki": MCP (§8) already gives assistants permission-aware
      retrieval under the user's own token. Is a built-in assistant UI still
      wanted on top? Same non-negotiable either way: answers only from pages
      the asker can view.
- [ ] MCP write tools: should agents ever create or edit pages, and under
      what review process? v1 is deliberately read-only.
- [ ] Email/digest notifications as well as in-app, or in-app only? (Email
      leaves the app's control — page titles would travel to a mail server.)
- [ ] **Live text carets require CRDT** (§8). Promote co-editing to a
      committed milestone — deciding relay-only vs server-side Ycs, plus
      multi-author revisions and session-scoped audit — or ship v1 with
      viewer presence and mouse pointers only?
- [ ] Is live presence ("X is reading this page right now") acceptable in a
      controlled environment, or does it need an opt-out / invisible mode?
