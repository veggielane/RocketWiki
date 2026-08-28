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
| Tables | GFM pipe tables with column alignment (`:---` / `:---:` / `---:` delimiters) and merged cells via MultiMarkdown table syntax (markdown-it-multimd-table): colspan as an adjacent-pipe merge (`\| wide \|\| x \|`), rowspan as `^^` continuation cells, both combinable; newlines inside cells as literal `<br>`. Cells are single logical lines of inline content; an empty cell keeps spaces between pipes (`\|  \|`) to stay distinct from a colspan merge, and literal `^^` / `\|` cell text is backslash-escaped. Table captions and multi-row headers are outside the feature set and rejected rather than reshaped |
| Code blocks with language | Fenced code blocks |
| Task lists | GFM `- [ ]` |
| Images / attachments | `![alt](attachment://{id})` — resolved to URLs at render time |
| Callouts (info/warning/note) | Directive syntax: `:::info … :::` |
| Page links | `[title](page://{id})` — stable across renames |
| Mentions | `@[display](user://{id})` |
| Diagrams | ` ```mermaid ` fenced block (source rendered client-side); ` ```drawio ` fenced block whose body is base64 of the diagrams.net editable-SVG export — one payload renders as an inert data-URI image and reloads into the embed editor. The drawio body may begin with one optional `alt: <text>` line (author-supplied alt text for the rendered diagram; `:` isn't base64, so it can't collide with a payload, and an absent line is the old format byte-identically); mermaid alt text uses mermaid's own `accTitle:`/`accDescr:` directives, part of the fence source. Both fences stay plain text to the serializer, sync bundles (§12), and the importer (§13); `drawio` is a reserved fence language. Per-diagram payload cap: 512 KB of base64 |
| GitLab references (§18) | `[text](gitlab-issue://{project}/{iid})` link mark; ` ```gitlab-file ` and ` ```gitlab-issues ` fences (reserved languages, `key=value` bodies) — host-free scheme forms, inert text to every pipeline but the SPA |
| Custom emojis (§19) | `:name:` where the name matches `[a-z0-9_-]{1,64}` **and exists in this instance's emoji registry**; anything else is literal text. Plain TEXT to the serializer, round-trip suite, sync bundles (§12), and importer (§13) — no mark, no node, zero pipeline changes. Deleting a definition leaves content rendering the literal text — harmless by construction |

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
                AND clearanceAllows(page's protective marking, principal)   [§21]
canEdit(page) = canView(page) AND spaceRole ≥ editor
                AND every edit-restriction on page + ancestors passes
comment       = requires canView
```

That third conjunct is the **protective marking** (§21): every page carries a UK
Government classification and an optional eyes-only caveat, and it gates view
access rather than merely being displayed. It is a third kind of thing on top of
grants and restrictions, not a variant of either, and its defining property is
that it can only ever **subtract** — no grant, no restriction, and no role widens
a marking. It is applied inside the same `EffectivePermissionCalculator.Compute`
that evaluates the two rule kinds above, so every read path inherits it
structurally rather than by remembering. Full treatment in §21.

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
- **Page properties** are the third kind of per-page metadata and follow the
  label rule exactly — `canView` to read, `canEdit` on that page to set or
  remove, no restriction of their own — with one addition: the *key registry*
  they draw from is instance-admin vocabulary, like the attribute registry
  (§6.5). Full treatment in §20.
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

**Nor do they bypass a protective marking (§21).** The same rule, applied to the
third conjunct of `canView`: an instance admin without the clearance a page's
marking demands sees that page exactly as everyone else without it does — absent.
Unlike a rule, an admin cannot edit their way past it either: re-marking a page
requires `canEdit`, which already includes the clearance gate against the page's
current marking, so a page you cannot see is a page you cannot re-mark downward.
Clearance itself lives in Keycloak, not in RocketWiki, which is what keeps that
door shut.

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

**The recorded reason is deterministic.** A denial's reason names the *first
failing restriction*, so "first" has to mean something stable: restrictions
are always evaluated **root-most ancestor first, the page's own rules last**,
and by creation time within a single page. A denial therefore reports the
outermost boundary the caller failed, reports the same rule on every request,
and reports the same rule the permission inspector (§6.6) shows — the
inspector exists to explain audit rows, and the two disagreeing would make
both useless. Ordering never affects the *decision*: restrictions accumulate
as a conjunction, so which rule is named is the only thing that depends on
it. Enforcing this is a loading concern, not a calculator one — the
calculator states the input contract, and a single loader in the data layer
(`PermissionContextLoader`) is the only thing that assembles rule chains, so
a new call site cannot quietly reintroduce database-enumeration order.

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
| Action | `page.view`, `page.edit`, `page.move`, `space.browse`, `attachment.download`, `search.query`, `page.query`, `permission.change`, `audit.view`, `sync.import`, … |
| Subject | type + id (page, space, attachment, comment, rule) + space key |
| Outcome | `success` or `denied` |
| Request context | request id, client IP, channel (`graphql` / `mcp` / `attachment`) + MCP client name |
| Details (JSON) | revision created, search query text, rule before/after, failing restriction on a denial |

The `search.query` row's Details JSON carries the raw query text, facets,
and result count — the audit table, not telemetry (§15), is where
who-searched-what lives.

`page.query` (§22) is RQL's equivalent: one row per executed query, Details
carrying the raw RQL string and the visible result count, on the same reasoning.
It dedups on the query text rather than on a subject, so a page embedding several
list widgets writes a row per query rather than one for the whole request — the
same discriminator `gitlab.fetch` uses for two resources in one document. Invalid
queries are audited too, still as `success`: `denied` stays reserved for ABAC
refusals, and RQL never produces one (restricted pages are absent from the
candidate set, never refused). `parseRql` is deliberately unaudited — a pure
syntax service with no subject and no access decision (§22.7).

`settings.avatar.set` / `settings.avatar.cleared` (§19) — the avatar
mutations, via the domain-event pipeline in the same transaction as the
row, on the `attachment` channel (the shared binary-HTTP surface; the
action names carry the distinction). No subject and no details: a per-user
setting fits no content SubjectType, the row's UserId already names whose
avatar, and nothing about the image belongs in an audit row.
`emoji.created` / `emoji.deleted` (§19) — the instance-admin registry
mutations; subject null, emoji name in details; route-gate denials audited
under the same actions. Reading avatars or emoji images is deliberately
unaudited on every path — display assets, same reasoning as display-name
resolution — and the anonymous `/avatar/{hash}` endpoint has no principal,
so no row *can* exist. Note also: the audit *channel* `attachment` now
denotes the binary-HTTP surface (`/attachments`, `/avatars`, `/emojis`),
not the Attachment subject.

`page.edit_session.joined` / `page.edit_session.left` (§8 co-editing) —
subject = page, on the `realtime` channel (a hub invocation reaches no HTTP
path; filing it under `graphql` would lie about the transport). Join
denials are recorded Denied with the failing reason while the caller still
sees the not-found-identical null; `.left` details carry
left/disconnected/evicted. Presence joins deliberately remain unaudited —
joining an *edit* session consumes a canEdit authorization and opens a
content-bearing channel, which is what makes it §7-worthy. The save's
`page.edit` details now include `contributors` for session saves.

`assistant.ask` (§9.5) — every ask, one row, Details carrying the question
text (exactly as `search.query` carries its query text), the disposition,
the ids of pages whose content was sent to the model, the validated cited
page ids, and the answer's aggregate marking label (§21.13) as it stood when
the answer left — not recoverable later from the page ids, because a marking
is a mutable row and re-deriving it after a re-marking would report today's
classification for yesterday's answer. Outcome is always `success` — an unavailable result is
still a completed ask, and `denied` stays reserved for ABAC refusals, which
retrieval already enforced by making restricted pages absent; the race-only
mid-retrieval denial audits as an ordinary `page.view` Denied row.
Anonymous asks are refused before any work with no row. Telemetry
(`rocketwiki.assistant.*`) sees dispositions, counts and durations only.

`page.marking.set` / `page.marking.downgrade` (§21) — a page's protective
marking changed, subject = page, details carrying the full before-and-after
marking. Two action names for one mutation on purpose: downgrading (anything
that lets somebody read the page who could not before) is the operationally
risky direction, and its own action name is what lets a reviewer find every
widening in the estate with one query. Like access rules, the audit log is the
*only* history a marking has — the row itself is mutable — so the before-state
is load-bearing, not decoration. This is also the only place an eyes-only
country set is written out in full; §15 keeps it out of every telemetry tag.

`gitlab.fetch` (§18) — every GitLab read, one row per distinct resource per
request (same resource twice in one document is one row, like `page.view`).
Details carry the *reference* (project, iid/path/ref or the filter) and the
outcome — exactly as `search.query` carries its query text — and never the
fetched content; issue titles and file bodies are GitLab's data, and the
wiki records that the fetch happened, not what came back. Outcome is always
`success`: an upstream 403/404 is GitLab's decision about its own content,
not a wiki access decision refusing a principal, so `denied` stays reserved
for ABAC refusals (the same reasoning that keeps `sync.import.refused` out
of `denied`). `settings.gitlab_token.set` / `.cleared` audit the Settings
mutations via the domain-event pipeline, with no token material in any row.

### Emission and guarantees

- Implemented in the GraphQL execution pipeline: every root query/mutation
  field declares its audit action and subject, and a schema test fails the
  build if one ships without a declaration — same enforcement style as the
  Markdown round-trip rule. Nested reads are covered too: resolving a page's
  content emits `page.view` wherever it appears in a query (§8). The
  declaration guard covers all four channels, not just GraphQL roots: every
  MCP tool, every client-invokable hub method, and every minimal-API route
  handler on the `*Endpoints` classes must carry exactly one of
  `[AuditAction]`/`[NoAudit(reason)]`, enforced by AuditCoverageTests (route
  handlers are named static methods returning `Task<IResult>` by convention —
  the sweep fails a class whose handlers become unsweepable lambdas).
  Deliberate non-audits — the avatar and emoji image GETs, the anonymous
  Gravatar endpoint — are machine-checkable declarations with their reasons
  inline, not prose. Every channel lands in the same audit table.
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
- Append-only is enforced at the database: the app's SQL login gets
  INSERT/SELECT only on this table, no UPDATE or DELETE grants. **Status:
  designed, not yet built** — the grant DDL will be enforced once the
  partition/grant migration ships (it is server/security-principal-level DDL
  outside the EF model; see the TODO in `AuditEventConfiguration`, §14, and
  §16). Until then append-only holds by application convention only.
- Sign-in/out events live in Keycloak's own event log; RocketWiki audits
  application actions and correlates by user id.
- Audit is a **database table** (`AuditEvent`), not a log stream — queryable,
  and by design partitioned and grant-protected (partitions/grants: DDL
  pending, see the status note above). Operational telemetry (§15) is a
  separate concern and deliberately carries no content or identity detail.
- **Rule changes record full before-and-after state**, not diffs. SQL Server
  temporal tables were rejected because SQLite cannot emulate them and the
  test tier must exercise the real schema (data-model.md, *Temporal tables —
  considered and rejected*). That makes the audit log the *only* record of
  rule history, so it must be complete enough to reconstruct the rule set at
  any past instant by replay. Enforced by test.

### Volume and access

- Page views dominate volume. The table is designed to be date-partitioned,
  with an archival job moving old partitions to cold storage (retention
  period: open question) — but the monthly-partition DDL is not yet built
  (same status as the grants above; §14/§16 track it). Today the shipped
  migration creates an ordinary clustered index on `(TimestampUtc, Id)`.
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

#### CRDT co-editing

**Resolved: promoted to a committed milestone, relay-only.** The hub relays
opaque Yjs binary updates between edit-session members and retains a
session-scoped in-memory update log for late joiners; the server never
interprets CRDT state (Ycs, the C# port, is unmaintained — last activity
Aug 2023, Yjs 13.4.14-era). Consequence, accepted: the server cannot
validate live update content. Authorization is therefore membership-gated —
joining a session requires canEdit, evaluated at join with the same silent
fail-closed shape as presence joins and re-checked by the rule-change
eviction sweep — and the authoritative write remains the existing save
path: a client serializes to Markdown and calls updatePageContent, so
nothing enters storage, sync, search, or embeddings except through the
guarded pipeline. The server designates the first joiner as seeder (it
seeds the Y.Doc from CurrentContent at a stated base revision and pushes
the encoded seed as the log's first entry); seeder loss re-designates; an
empty session's log is dropped after a grace period; a log byte cap forces
a save-and-reseed (the designated member saves, then hands back one
full-state snapshot that replaces the log). Sessions are memory-only: an
API restart drops them and clients re-seed from saved content — the durable
record is the save path, not the relay. Multi-author revisions: the session
tracks distinct contributors since the last save; the save records them as
PageRevisionContributor rows in the same transaction (AuthorUserId stays
"who pressed save"), resolved exclusively from the server's session
registry — no client-supplied contributor list exists anywhere in the API.
Replicas refuse co-editing at the join gate (§12); presence and pointers
still work on a replica — there's just nothing to merge. Live updates are
content: telemetry sees byte counts only (§15).

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
  and the vector in SQL Server 2025's native **`vector(1536)`** column type,
  EF-mapped via `SqlVector<float>` (provider-conditional: SQLite keeps the
  float-blob converter). Cosine scoring is in-engine, exact
  `VECTOR_DISTANCE` — which per its own documentation **never uses a vector
  index, even if one exists**. The DiskANN index is deliberately deferred
  with three engine-verified reasons (see the
  AlterPageEmbeddingToNativeVector migration's doc-comment), plus a fourth
  the first real CI run discovered (error 42217): the engine requires the
  indexed table to have a clustered PK on a single 4-byte INT column, which
  `PageEmbeddings`' key deliberately is not. The doc-sourced three: index
  creation requires ≥100 non-NULL rows (a from-zero migration chain always
  fails it), on boxed SQL Server 2025 the index is preview-gated AND makes
  the indexed table read-only (the full-DML index format is currently
  Azure SQL/Fabric only — which would kill the §9.2 background indexer),
  and index-assisted ANN needs the separate preview `VECTOR_SEARCH` TVF
  anyway. The limitations are **tripwire-tested** in the SQL Server tier
  (the read-only probe runs on a synthetic table shaped to satisfy the
  preconditions) so the day an engine build lifts them, CI says so —
  detection, not hope. Model name +
  dimensions are stamped per row, and a startup guard fails the host loudly
  when configured dimensions disagree with the column.
- Hybrid query: over-fetch top-K from FTS and vector search, fuse with
  **reciprocal rank fusion**, then filter by `canView` (§6.7) and return the
  top N. Over-fetching before the permission filter keeps restricted-heavy
  result sets from coming back empty.
- Behind `ISearchService` like everything provider-specific: SQLite
  integration tests use the LIKE fallback plus in-memory cosine similarity;
  the real FTS + native-vector path is covered by the SQL Server
  Testcontainers suite (§14).

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
assistant does the generation over permission-aware retrieval. A built-in
assistant now exists as well — §9.5.

### 9.5 Ask the wiki

A built-in assistant answers questions from wiki content — resolving §17's
open bullet: the MCP path (§8) remains for users' own agents, and a
built-in `askWiki` GraphQL field now exists for everyone else. The
non-negotiable is the same either way and is enforced by construction, not
by prompt: **answers only from pages the asker can view.** Retrieval runs
under the caller's own principal through the same permission-filtered
hybrid search as §9.3 (`ISearchService`) and the same canView-gated content
loads as every resolver (`IPageReadService`, pattern-matched `ReadResult`);
a page the asker cannot view is silently absent from the model context, so
the model cannot leak what retrieval never saw. Context chunks come from
the §9.2 chunker (same heading anchors as search deep links), capped by a
configured char budget and rank-ordered; the model is instructed to answer
only from the provided context and cite positional `[Sn]` markers, which
the server validates against the exact context it issued — an in-range
marker is a viewable section by construction, and fabricated markers are
stripped. Zero retrieved content means the model is never called
(`NO_RESULTS`); the LLM is plumbing, access control is the feature.

**The answer carries an aggregate protective marking** (§21.13): the highest
classification among *everything that entered the model context*, cited or
not, with each distinct eyes-only caveat listed rather than merged. An answer
drawn from a `UK SECRET` page is `UK SECRET`, because the model launders the
marking off the content and something has to put it back on. Each citation
additionally carries its own source page's marking. Both are display labels
computed after enforcement — retrieval already ran under the caller's
principal — and anything rendering the answer must render `aggregateMarking.
label` beside it. `aggregateMarking` is null exactly when `answer` is: no
text, nothing to mark.

The chat endpoint is configured like the embedding endpoint and carries the
same honesty: **the user's question and the retrieved (viewable) page
content travel to it** — that is the feature — so it is inside the security
boundary (§9.4), reached via the Aspire `assistant` connection string or
`Ai:ChatModel` against the shared `Ai:BaseUrl`, and fail-closed (§15): no
default exists, unset means the feature is absent and `askWiki` answers a
typed `NOT_CONFIGURED` payload fact (never a GraphQL error — the §18
degradation pattern, including `UNREACHABLE` on endpoint failure: one
attempt, bounded timeout, no retries). v1 is non-streaming and stateless:
no conversation memory, each ask retrieves fresh under the current token.

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

Three providers, selected by configuration:

- **`S3FileStorage`** — `AWSSDK.S3` with a configurable `ServiceURL` and
  path-style addressing, so any S3-compatible endpoint works, not just AWS.
- **`FileSystemFileStorage`** — a directory on disk. Used by local dev and
  integration tests; also fine for the simplest single-box deployments.
- **`SqlServerFileStorage`** — blob bytes as `varbinary(max)` rows. **Not the
  production default and not recommended as one**, for reasons worth stating
  rather than implying: every byte written passes through the transaction
  log, every backup carries the blobs, large reads evict pages from the
  buffer pool, per-GB database storage is the most expensive storage in the
  deployment, and there is no CDN or offload path in front of it. It exists
  because some deployments want exactly those costs in exchange for one
  property the other two cannot offer: content and blobs in a single backup
  at a single point in time. That buys a genuinely simpler air-gapped or
  single-container install, a dev/test environment with no MinIO and no
  mounted volume, and a restore drill that is one restore instead of two —
  with no window in which the database and the object store disagree about
  which attachments exist.

The SqlServer provider's table is created on first use and sits **outside the
EF Core migration chain**, deliberately: `RocketWiki.Storage` references
neither EF Core nor `RocketWiki.Data` (the dependency runs the other way), an
opt-in provider must not add a table to every deployment that will never
select it, and the checked-in migration set is asserted by `MigrationTests`.
Its connection string defaults to the application's own
(`ConnectionStrings:rocketwiki`) — sharing one database is the point — but
pointing it at a *separate* database is supported and is often wiser, since
blob churn then lands in its own transaction log rather than the one carrying
page edits. The key column carries a binary collation so that "same key"
means the same thing here as on S3 and on a case-sensitive filesystem.

Reads and writes both stream: a sequential-access reader hands back the blob
without materializing the row, and uploads append in chunks rather than
buffering the attachment. One consequence has no equivalent on S3: an
interrupted upload leaves a *truncated* object rather than none, because each
chunk commits on its own — the same exposure `FileSystemFileStorage` has with
`FileMode.Create`, and the alternative (one transaction spanning a 100 MiB
upload) is precisely what this provider exists to avoid.

Storage keys are opaque (`attachments/{yyyy}/{MM}/{guid}`) — page moves and
renames never touch storage; the `Attachment` row owns all meaning.

### Rules that interact with access control (§6) and audit (§7)

- **No presigned URLs.** Downloads always stream through the API: a presigned
  URL would bypass both page restrictions (`canView`) and the audit log. The
  perf cost is acceptable at wiki scale. The rule is enforced by a tripwire
  test that pins `IFileStorage` to its four streaming members and fails on any
  member whose name suggests minting a URL.
- **The three binary routes answer alike.** Attachments, avatars and emojis
  share one domain-error→status map, one 413 shape, and one upload-cap guard
  (`Api/Http/BinaryRoutes.cs`) — they had drifted into three partial maps
  where a given error kind mapped correctly on at most one route. Replica
  refusals are `403` everywhere, never `400`; a name collision is `409`. The
  shared cap guard also gives the multipart routes the in-handler streaming
  bound that only the emoji route had, which matters because the Kestrel
  body-size feature it previously relied on is absent under TestServer.
- **Download-response hardening.** The attachment download carries
  `X-Content-Type-Options: nosniff`, `Content-Disposition: attachment`, and
  `Cache-Control: private, no-cache` with a strong ETag (the attachment id —
  blobs are immutable per id). `no-cache` rather than a freshness window is
  deliberate: every reuse revalidates through the API, so `canView` runs and
  the §7 row is written even for a 304; a `max-age` would create unaudited
  reads. All three storage providers validate every key before any I/O via a
  shared helper (rejecting separators, rooted forms, and dot segments) —
  defense in depth, since keys are system-generated — and the filesystem
  provider additionally proves the resolved path stays under its root.
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
  "Provider": "S3",                          // or "FileSystem", or "SqlServer"
  "S3": { "ServiceUrl": "http://minio:9000",
          "Bucket": "rocketwiki", "ForcePathStyle": true },
  "FileSystem": { "Root": "/data/attachments" },
  "SqlServer": { "ConnectionString": null }   // null: share ConnectionStrings:rocketwiki
}
```

---

## 11. Authentication flow

The numbered subsections are the runtime order: a request's identity passes
through each in turn.

### 11.1 SPA sign-in

SPA uses `react-oidc-context` (oidc-client-ts) for Authorization Code + PKCE
against Keycloak; tokens kept in memory, silent renew via refresh token.

If token-in-browser is ever deemed unacceptable, the fallback is a BFF with
cookie auth — noted as an option, not planned.

### 11.2 API token validation

API validates bearer JWTs against Keycloak's JWKS
(`AddJwtBearer` with authority = realm URL).

MCP clients (§8) authenticate against the same Keycloak realm via OAuth 2.1
and hit the same bearer-token validation — one auth path for every channel.

### 11.3 JIT user provisioning

On each authenticated request, middleware upserts the local `User` row from
claims (JIT provisioning), including registered attributes (§6.2).

### 11.4 The request principal

The request principal (groups + attributes) is built from the token and
handed to the access-rule evaluator (§6) — the local mirror is never used
for authorization decisions.

### 11.5 Keycloak realm requirements

Keycloak setup required: a `groups` protocol mapper and one mapper per
registered attribute (e.g. `nationality`) on the RocketWiki client, so the
claims actually appear in access tokens.

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
  beats every grant (§6.4). The UI shows a "Replica of {origin} — read-only"
  banner; mutations fail with a typed `ReadOnlyReplicaError` (§8). UI
  terminology: a synced space is a *replica* everywhere user-facing —
  "mirror"/"mirrored" wording was retired in the UX polish round, and
  degraded-state copy across features is centralized in
  `web/src/feedback/unavailableCopy.ts`.
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
| Page **protective markings** (§21) — a page that is SECRET on low is SECRET wherever it lands. Same fail-closed reading as restrictions: an eyes-only country the high side's nationality vocabulary doesn't recognize matches nobody, so the page arrives *more* restricted. A page can never land unmarked — an upsert carrying no marking creates the row at TOP SECRET | Clearance itself — each side's Keycloak decides who holds what (§21.3), exactly as it decides group membership |
| | Space **lifecycle and identity** — name, description, archived state. Spaces aren't a sync event type: import creates the replica row from the space key alone, so renaming or archiving a replica is legitimate local curation (like grants), not a blocked content write |
| | Custom emoji **definitions** (§19) — content carrying `:name:` syncs as plain text and degrades to literal text on an instance whose registry lacks the name. Syncing the registry is a flagged future decision (collision question: same name, different image, different instances). User avatars likewise never travel |

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
the space id and its `OriginInstanceId`, so the client can render "Replica
of {origin} — read-only" rather than a bare refusal.

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

The importer converts tables at full §4 fidelity: merged cells become
MultiMarkdown spans (adjacent-pipe colspans, `^^` rowspan continuations),
per-cell alignment becomes GFM per-column delimiter colons (header cell
wins, else body majority), and in-cell line breaks become literal `<br>` —
all byte-compatible with the editor serializer and enforced by the
regenerated converted-markdown corpus. The only table shape that still
degrades is a rowspan crossing the header/body boundary, which is split
(header keeps the content, covered body cells are emptied) and flagged in
the conversion report; multi-row headers, captions, and block content
inside cells are likewise flattened with a report note rather than
silently reshaped.

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
│   ├── RocketWiki.Sync.Tests/     bundle export/import CLI
│   ├── RocketWiki.Importer.Tests/ Confluence import pipeline
│   └── RocketWiki.SqlServer.Tests/ provider-specific (Testcontainers; CI's `sqlserver` job)
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
3. **Provider-specific tests** — `tests/RocketWiki.SqlServer.Tests`, a
   smaller suite against real SQL Server 2025 via Testcontainers (a derived
   `mssql/server:2025-latest` image with `mssql-server-fts` installed — the
   stock image cannot apply the InitialCreate migration's FULLTEXT DDL).
   One container per run, one database per test, schema from the real
   checked-in migrations. Covers what SQLite cannot emulate: the migrations
   themselves, Full-Text queries (CONTAINSTABLE with inflectional
   stemming), migrate-on-startup through the real API host, and the most
   dialect-sensitive service behaviors (outbox sequence uniqueness under
   racing writers, audit-transaction rollback, CHECK/length enforcement).
   Docker presence is the switch: without a daemon the whole project skips
   visibly and the solution stays green; CI's `sqlserver` job is the tier's
   first-class home and fails if any of its tests skip there. Native
   `vector` + in-engine `VECTOR_DISTANCE` search landed and are exercised
   here, including the tripwire tests that detect the engine lifting its
   DiskANN limits; the DiskANN index itself (deferred, §9.3) and audit
   partitioning/grants remain the tier's next
   tenants once their DDL exists. This supersedes the earlier intent to
   drive this tier through `Aspire.Hosting.Testing`; the AppHost topology
   itself is still only exercised by `aspire run`.

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
build rather than shipping.

Logs were long described here as uninterceptable. That was wrong, and the
correction matters: log records *are* interceptable in-process — an in-memory
`BaseExporter<LogRecord>` in a test host is exactly how the gravatar guard
below works. What is genuinely impossible is filtering *inside* the
OpenTelemetry pipeline: processors compose into a `CompositeProcessor` whose
`OnEnd` calls every child unconditionally, so a processor cannot drop a
record, and scopes are readable only through `ForEachScope` with no setter,
so it cannot de-scope one either (established against OpenTelemetry 1.15.3
and still true on 1.18.0, where the guard test below keeps passing — a
processor that blanked attributes, body, and formatted message still exported
the `RequestPath` scope). Filtering therefore has to happen **ahead of the
provider**, via `AddFilter<OpenTelemetryLoggerProvider>`. So: content in log
*messages* remains a review rule, but the one identity-derived path is closed
structurally (§19), and the closure is pinned by a test that fails when the
filter is removed.

That review rule rests on a posture worth stating outright: **the backend
logs almost nothing, by design.** Traces, metrics, and the audit table (§7)
are the observability story; a log line is the one emission channel the
hygiene test cannot intercept, so the safest log statement is the one never
written. `RocketWiki.Core`, `RocketWiki.Storage`, `RocketWiki.Sync`, and the
Importer contain no log statements at all; the few sites that exist (a
handful of error/warning paths in the API and Data projects — blob-missing
500s, embedding failures) carry only identifiers, enum names, and exception
*type* names — never titles, content, storage paths, query text, or
principal attributes. Adding a log site gets the same §15 review as adding a
span tag, and the burden of proof sits on the addition.

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

`GitLab:BaseUrl` (§18) follows the same fail-closed rule: **no default
exists**, and unset means the feature is absent — every GitLab field
answers `NOT_CONFIGURED`. This URL is where users' GitLab credentials are
sent, so a defaulted or guessed value would be a credential-exfiltration
bug, not a convenience. GitLab telemetry is `rocketwiki.gitlab.*` with
bounded tags only — operation (`issue`/`issues`/`file`), outcome (fixed
vocabulary), upstream status *class* (`2xx`/`4xx`/`5xx`/`none`) — never a
project path, file path, issue title, or filter text; those belong in the
`gitlab.fetch` audit row (§7), nowhere else. Two built-in leaks are closed
structurally rather than by hope: the gitlab HttpClient's handler sets
`ActivityHeadersPropagator = null`, which removes the DiagnosticsHandler
entirely — no built-in client span (whose `url.full` would carry repository
file paths), and no W3C trace context handed to GitLab (consistent with
"propagated only to the API's own origin") — and the HttpClientFactory's
default request logging (full URI at Information) is removed for this
client, the one leak channel the hygiene test cannot intercept. The bounded
`rocketwiki.gitlab.fetch` span replaces the suppressed one.

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
OpenTelemetry's lowercase dotted convention — and that rule is enforced too,
not just stated: `TelemetryNamingTests` reflects over every telemetry class
and fails on any instrument name or `*Tag` constant that doesn't match.
Writing it surfaced the one historical offender, an area-less
`rocketwiki.outcome` (now `rocketwiki.data.outcome`), which had made
Data-layer outcomes ungroupable alongside every other area's.

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
| 4 | Search & polish | **done** | `search`/`labels` API matching the shipped UI operations, permission-filtered with section attribution; the SQL Server FTS path (CONTAINSTABLE, inflectional stemming) is CI-verified against a real FTS-enabled engine by the §14 Testcontainers tier on every run — the `sqlserver` job fails if the tier skips — while the SQLite LIKE fallback is what the container-free tiers exercise; trash/restore, space management UI, rule builder + permission inspector, audit log viewer, import report UI |
| 4b | Notifications & presence | **done** (live hub unexercised) | SignalR hub, watches, delta-based mentions and reply notifications, per-recipient `canView` fan-out (re-checked at read time too), persisted notification list incl. `sync_bundle_landed` rows from the offline import. The SPA now generates its client from the exported `schema.graphql` (placeholder deleted), runs the real SignalR transports by default (fakes only behind `VITE_FAKE_REALTIME`, for tests and backend-less dev), and wires the bell (persisted list + live push, de-duplicated by row id), watch/unwatch on pages and spaces, and the §12 admin sync status page. Per the standing caveat no browser has ever actually connected to the hub |
| 5 | Migration | not started | Importer against a real Confluence space export; trial runs and fidelity review |
| 6 | Low/high sync | **done** (baselines are current-state-only) | Outbox journal, `RocketWiki.Sync` export/import CLI with hash chain, baseline snapshots (documented simplification: no revision history), replica read-only enforcement with `originInstanceId` in the error, admin `syncStatus` query |
| 7 | Semantic search | **done** (fake endpoint; exact-scan vectors) | Heading-boundary chunker over the shared anchor primitives; `PageEmbeddingState`-driven polling background job (covers sync-CLI writes; per-chunk hash re-embed; failure backoff; trash purge); `IEmbeddingGenerator` via Microsoft.Extensions.AI.OpenAI from the Aspire `embeddings` connection string — unconfigured means keyword-only, structurally; hybrid RRF inside the same `search` field (no schema change), canView after fusion, semantic hits deep-link via chunk attribution recomputed post-canView; `rocketwiki.embeddings.*` telemetry with a sentinel hygiene test. Native `vector(1536)` shipped (the AlterPageEmbeddingToNativeVector migration) with in-engine `VECTOR_DISTANCE` scoring, CI-verified by the §14 Testcontainers tier; only the DiskANN index remains deferred, with engine-verified, tripwire-tested blockers (§9.3, data-model.md); SQLite maps the column to a blob and keeps the in-memory cosine fallback |
| 8 | MCP server | **done** (no live Keycloak/OAuth dance yet) | `/mcp` (streamable HTTP, stateless, in-process) via the official C# SDK; RFC 9728 resource-metadata discovery pointing at Keycloak; four read-only tools over the shared service layer; per-call `mcp`-channel audit incl. client name and denied-read reasons; audit-declaration guard extended to tools; `rocketwiki.mcp.*` telemetry |
| 9 | k3s deployment | **authored, unexercised** | Dockerfiles + Helm chart in-repo (`deploy/`), migration Job via EF bundle, Traefik ingress with WebSocket upgrade, secrets by reference, probes (TCP until health endpoints get a non-Dev config gate), offline image path. `helm lint`/`template` pass; nothing applied to a cluster; restore drill unrun |
| 10 | Co-editing | **done** (live hub unexercised) | Relay-only Yjs edit sessions over the existing hub: canEdit-gated join with denied-join auditing, seeder designation + reseed protocol, log cap + empty-session GC, rule-change eviction extended to edit groups, PageRevisionContributor attribution wired through updatePageContent (forgery-proof: server-side session data only), session-scoped audit on the new realtime channel, `rocketwiki.coedit.*` telemetry with sentinel hygiene test. SPA phase 2: SignalR Yjs provider over the shared hub connection (join/seed/replay, batched updates, awareness carets, log-cap auto-save-and-reseed, eviction, documented reconnect), collaborative TipTap mode with solo fallback as the default degradation, session-base saves with contributor attribution surfaced on save, presence pointers on the edit route |

### The standing caveat

Everything above is verified by **tests**, not by running. No container has
ever started in development: no `aspire run`, no migration applied to real
SQL Server, no Keycloak realm imported, no token decoded to confirm the
`groups` and `nationality` claims actually arrive, no S3 call against a live
bucket. Full-text search and the native `vector` type were once part of this
gap; both now ship in the checked-in migrations and are exercised against a
real engine by the §14 Testcontainers tier on every CI run. What remains
SQL-Server-only and unbuilt is narrower: the DiskANN vector index
(deliberately deferred with engine-verified, tripwire-tested blockers —
§9.3) and the audit partitioning/append-only grants, whose DDL is not yet
written (§7, §14).

The SQL Server slice of that gap now closes on every CI run — the §14
Testcontainers tier applies the real migrations and exercises FTS against a
real engine in the `sqlserver` job — but only in CI; no container has yet
run on a developer machine. The rest of the gap closes the day a local
container runtime works, and closing it is
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
- [x] Does each network have a container registry mirror for the published
      images (SQL Server, MinIO, nginx, api/web), or must deployment ship
      image tarballs? — resolved: the deploy package supports both;
      `deploy/README.md` documents the registry-mirror path and the
      air-gapped tarball path as Path A / Path B. Which one a given network
      uses stays a per-network operational choice, not a design question.
- [x] k3s: does SQL Server run inside the cluster on a PVC, or outside it?
      — resolved: outside. The Helm chart does not run SQL Server, period
      (`deploy/helm/rocketwiki/values.yaml` says exactly that); the cluster
      consumes an external server via the operator-created connection-string
      Secret.
- [x] k3s: one API replica (simplest — no backplane, no migration Job) or
      scale-out from the start? — resolved: one replica, hard-locked.
      `values.schema.json` caps `api.replicaCount` at exactly 1 and the
      Deployment template fails the render on anything else; no Redis
      dependency. Scale-out is a deliberate future chart change (backplane
      included), not a values tweak.
- [x] "Ask the wiki" — resolved: both. MCP (§8) stays the path for users'
      own assistants; a built-in `askWiki` field (§9.5) now does RAG
      server-side under the caller's principal against a fail-closed
      in-network chat endpoint. Same non-negotiable, enforced by
      construction: answers only from pages the asker can view — the model
      context is built exclusively from permission-filtered retrieval, so
      restricted content never reaches the model at all.
- [ ] MCP write tools: should agents ever create or edit pages, and under
      what review process? v1 is deliberately read-only.
- [ ] Email/digest notifications as well as in-app, or in-app only? (Email
      leaves the app's control — page titles would travel to a mail server.)
- [x] **Live text carets require CRDT** (§8) — resolved: promoted to a
      committed milestone, relay-only (see §8's co-editing decision note);
      multi-author revisions land as PageRevisionContributor rows and
      sessions audit as `page.edit_session.joined`/`.left` on the
      `realtime` channel.
- [ ] Is live presence ("X is reading this page right now") acceptable in a
      controlled environment, or does it need an opt-out / invisible mode?

---

## 18. GitLab integration

Pages can embed GitLab repository files, link issues with live status, and
list issues by filter. GitLab is a separate system with its own permission
model, so the integration is governed by one load-bearing rule and three
consequences of it.

### Every fetch runs as the calling user — no service account, ever

A shared service-account credential would make the wiki a read-around: any
issue or file the service account could see would render for any wiki user
who pastes a reference to it, and GitLab's own authorization — which may
embody the same export-control rules as ours — would be bypassed by exactly
the mechanism §6.5 forbids for our own content. So every GitLab fetch
carries the **calling user's own GitLab credential**, and a user with no
credential gets a placeholder, not someone else's view. No standing
broad-access credential exists anywhere in the system; the adversarial case
(user B's fetch ever carrying user A's token) is pinned by test.

**v1 mechanism: per-user personal access tokens**, stored encrypted at rest
(ASP.NET Data Protection, purpose-versioned) in a `GitLabCredentials` row
keyed by user, entered through a Settings surface (`setGitLabToken` /
`clearGitLabToken`, status via `gitlabStatus.viewerHasToken`). The token is
write-only by construction: no query, payload, or error anywhere in the
schema returns stored token material — enforced by a test over the exported
SDL — and the single decrypting read path hands the value straight into the
outbound request header. Set/clear flow through the domain-event pipeline
(`settings.gitlab_token.set` / `.cleared`, committed in the same
transaction as the row), and the domain events are structurally unable to
carry token material because it never enters them. Trade-offs accepted with
PATs: users must mint and paste a token (onboarding friction), tokens are
as broad as the user scopes them (recommend `read_api`), and revocation is
manual in GitLab. Data Protection key custody is a deployment concern: in
k3s the key ring must be persisted deliberately or every pod restart
silently invalidates stored tokens — degrading to "no credential", not
broken pages, but a support headache.

**The shared-Keycloak question, answered honestly.** GitLab may
authenticate against the same Keycloak realm as the wiki (§11). That makes
sign-on shared; it does **not** make Keycloak token exchange (RFC 8693) a
path to GitLab's API, because GitLab's REST API only accepts credentials
minted by *GitLab's own* authorization server — PATs, GitLab-issued OAuth
tokens, group/project tokens — never an external IdP's access token,
however trusted for login. The elegant path is therefore **RocketWiki
registered as an OAuth application in GitLab**: a per-user
authorization-code consent from the wiki's Settings page yielding a GitLab
access + refresh token pair stored server-side. A shared Keycloak makes
that consent hop nearly invisible (the user is already signed in to GitLab
via SSO), and it improves on PATs in every dimension — scoped by the app
registration, expiring, centrally revocable, zero token-pasting. It is
deliberately not v1 because it cannot be verified without a live GitLab to
register the application in and run the dance against (§16's standing
caveat applies with force). The design converges: the OAuth variant reuses
the same credential table shape and the same per-call credential flow —
only the credential *provider* and the outbound header change, both
isolated to one class each.

### Proxy-only: the browser never talks to GitLab

All GitLab reads flow through the API — `gitlabIssue`, `gitlabIssues`,
`gitlabFile` GraphQL fields resolving through a typed `IGitLabClient`
(GitLab REST v4) — the same rule as attachments (§10) and for the same
reasons: no side channel around the audit log, no GitLab credentials or
cookies in the browser's traffic, and the SPA's network boundary stays
exactly two origins (the API and Keycloak). The credential is a per-call
argument on `IGitLabClient`, never client state: a client that held a token
as a field would be one DI-lifetime bug away from cross-user reuse.

### Content forms: inert text to every existing pipeline

Three forms, all plain Markdown to the serializer, the round-trip suite,
sync bundles (§12), and the importer (§13) — **nothing is added to any of
those pipelines**; like ` ```drawio `, these are ordinary page text to
everything but the SPA:

| Feature | Markdown representation |
|---|---|
| Issue link | `[text](gitlab-issue://{project}/{iid})` — a link mark like `page://`; `{project}` is a numeric id or namespaced path, `{iid}` the final numeric segment |
| File embed | ` ```gitlab-file ` fence; body is `key=value` lines: `project=`, `path=` (required), `ref=` (optional, HEAD default) |
| Issue list | ` ```gitlab-issues ` fence; body is `key=value` lines mirroring the filter input: `project=`, `state=`, `labels=` (CSV), `search=`, `milestone=`, `orderBy=`, `sort=`, `first=` |

`gitlab-file` and `gitlab-issues` join `drawio` as reserved fence languages
(§4). **Bare-URL auto-detection was rejected**: a pasted URL carries a
hostname, which breaks the low→high crossing (the reference should mean
"this instance's GitLab", not "that hostname") and survives GitLab
migrations badly; and auto-rewriting pasted text violates round-trip
expectations. The editor *may* offer paste-time conversion of a GitLab URL
into the scheme form as an explicit affordance — but the stored form is
scheme-only, host-free.

### Live, never cached — and legible when it can't be live

Embeds are **live fetches at view time**. Nothing fetched from GitLab is
ever persisted, cached server-side, or snapshotted into page content — a
wiki page must never present stale GitLab state as current, and a cached
copy would also outlive the GitLab-side permission change that should have
hidden it. When live is impossible, the embed degrades to a **typed
placeholder showing the reference** (which is already the page's own
Markdown, so the placeholder discloses nothing new): `NOT_CONFIGURED` (no
`GitLab:BaseUrl` — the steady state on a high-side replica, whose network
cannot reach the low side's GitLab; if high runs its own GitLab, low-side
references simply resolve to `NOT_FOUND` there, which is the honest answer
there), `NO_CREDENTIAL`, `INVALID_CREDENTIAL` (401 — the user's token died;
the Settings surface is the fix), `NOT_FOUND` (403/404 collapsed — GitLab
itself blurs the two for unauthorized resources, and the wiki must not
sharpen a distinction upstream chose to blur), `UNREACHABLE`
(network/timeout/5xx). All of these are payload facts, never GraphQL
errors: an embed's failure must not eat the page around it. File content is
size-capped (`GitLab:MaxFileBytes`, default 512 KiB — checked against the
size header before the body is buffered, then re-verified while streaming
in case the header lies) and text-only (binary content returns metadata
with a typed `NOT_TEXT`); over-cap and non-text embeds still render their
metadata. Resilience is deliberately minimal: one attempt, short timeout
(`GitLab:TimeoutSeconds`, default 5), **no retries** — the standard
resilience handler is removed for this client, because a page with many
embeds retrying against a down GitLab is a retry storm at exactly the wrong
moment.

Dev tooling: the AppHost deliberately defines no GitLab resource — a GitLab
container is heavyweight (gigabytes, minutes to boot) and nothing in the
test tiers needs it; the SQLite tier fakes the wire under the real client.
A `gitlab/gitlab-ce` container behind `GitLab:BaseUrl` is a documented
local option for anyone who wants end-to-end dev, not part of the topology.

---

## 19. Profile pictures and the Gravatar endpoint

Users can upload a profile picture, shown beside comments, presence, and
bylines. Uploads (PNG, JPEG, or WebP) are normalized server-side — decoded
under hard limits (format allow-list, dimension gate before allocation,
capped decode memory), center-cropped, resized to a canonical 512×512, and
re-encoded to PNG. The user's original bytes are never stored: re-encoding
strips EXIF/XMP/ICC wholesale, so camera metadata such as GPS position
never reaches storage — worth stating because avatars are the most widely
served bytes in the system — and a polyglot file crafted to be both an
image and something else does not survive the decode/re-encode round trip.
SVG is never accepted (scripting risk), and nothing is ever content-sniffed:
avatars are served as `image/png` with `X-Content-Type-Options: nosniff`,
an ETag on the content hash, and short-lived cache headers. Bytes live in
object storage under an `avatars/` prefix with §10's exact discipline: no
presigned URLs, write-bytes-then-commit-row, orphans for the janitor.
Setting and clearing are self-only by construction (the routes take no
target user) and audited via the domain-event pipeline
(`settings.avatar.set`/`.cleared`); rendering another user's avatar
(`GET /users/{id}/avatar`, authenticated) is display data like `UserRef`
display-name resolution and deliberately emits no audit rows. Avatars are
**instance-local**: never synced, never exported; shadow users render
initials. Image processing uses SixLabors.ImageSharp — note for operators:
it is Six Labors Split License (Apache-2.0 terms for open-source/small-org
use, commercial license otherwise); we pin 3.1.12 because 4.x additionally
enforces a license key at build time — upgrading is a version bump plus a
provisioned `SixLaborsLicenseKey` once that question is settled.

The wiki can also act as a **Gravatar-protocol server**
(`GET /avatar/{hash}`, Libravatar-compatible) so other in-network tools —
GitLab above all — show the same faces. This cuts against every instinct in
this document and the tension is resolved by stating it, not hiding it: the
Gravatar protocol is **unauthenticated by design** — consumers fetch with
no credentials — so the endpoint is a deliberate exception to "all access
requires sign-in". It is therefore an explicit operator opt-in
(`Avatars:GravatarEndpointEnabled`, default false — the same fail-closed
posture as the OTLP endpoint, `VITE_DRAWIO_URL`, and `GitLab:BaseUrl`), and
§15's network boundary is the outer wall: "anonymous" means anonymous
*inside* the boundary, never the open internet. The inherent protocol
disclosures, plainly: with the flag on, any in-network actor without a wiki
account can fetch any user's avatar and can probe which email hashes have
one — that is what the protocol *is*, and enabling it is an operator's
decision that those disclosures are acceptable on that network. The design
confines the exception rather than pretending it away: lookups match the
MD5 *and* SHA-256 of the normalized (trim+lowercase) mirrored email
(historical Gravatar is MD5, the current spec SHA-256, Libravatar accepts
both), and JIT provisioning re-derives the stored hashes whenever it
refreshes the mirrored email, because a stale hash would serve the old
address-holder's face to whoever holds that address next. `d=404`
semantics are the default and only behavior — unknown hash, avatar-less
user, or feature disabled are one indistinguishable 404, so the flag state
is not probeable; other `d=` values are ignored (no server-side identicons
— the consumer's fallback owns misses). `s=` is honored, clamped to 16–512
and resized on demand from the stored canonical image (no pre-generated
variants: derived objects would need their own janitor and invalidation
story for a resize that costs milliseconds, and HTTP caching absorbs the
repeats). No JIT row, no principal, and no audit row exist on this path —
there is no acting user to attribute one to, and §7's vocabulary has no
anonymous case; the only telemetry is a bounded hit/miss/disabled counter,
and the route is excluded from HTTP tracing **and from exported logs**
wholesale, because a server span's `url.path`, the `RequestPath` logging
scope, and hosting's own "Request starting" message each carry the email
hash. The log side is an `AddFilter<OpenTelemetryLoggerProvider>` predicate
rather than a pipeline processor — see §15 for why a processor cannot do it
— and the one consequence worth knowing is that this route's blob-missing
operator error stays in local/console logs and is never exported. Beyond the flag there is no
rate limiting in v1 — stated as a fact, not an oversight. In-wiki
rendering does **not** depend on the flag: the authenticated
`GET /users/{id}/avatar` route serves the SPA regardless. Contrast with
§18: GitLab embeds carry per-user credentials because that content is
*someone's* controlled data; avatars are the opposite — deliberately
public-ish display data whose only gate is the network boundary and the
operator's opt-in.

**Custom emojis** share the binary-upload machinery: instance admins curate
a `:name:` registry (`POST`/`DELETE /emojis/{name}`, audited as
`emoji.created`/`emoji.deleted`; grammar `[a-z0-9_-]{1,64}`, lowercase-only
so uniqueness is case-insensitive by construction), any authenticated user
reads the list and the images (ETag/304). Uploads (PNG/JPEG/WebP/GIF) are
decode-limited, squared to 32–256 px, re-encoded with metadata stripped;
animated GIF is supported frame-preserving (64-frame cap); animated
PNG/WebP flatten to their first frame. Emoji definitions are
instance-local (§12): content carrying `:name:` syncs as plain text and
degrades to literal text where the registry lacks the name; syncing the
registry is a flagged future decision with an unresolved collision
question (same name, different image, different instances). Deleting a
definition leaves content rendering the literal text — harmless by
construction, and why deletes are hard deletes (a tombstone would only
block re-creating the name).

---

## 20. Page properties

Confluence-style **page properties**: a page carries a small set of key/value
metadata — `Owner: Ada Lovelace`, `Review Date: 2026-11-01`, `Status: Draft`
— shown beside the page rather than written into it. Values are plain text.
There are no types and no validation beyond length (1000 characters), which
is a deliberate v1 scope decision, not an oversight: typed properties bring a
type registry, per-type editors, coercion rules, and a migration story for
every type change, and none of that earns its keep before anyone has asked
what a date property should *do*.

**They are not page content, and that is the whole point.** Properties live
in their own tables and are edited on a dedicated properties screen. They
never enter the Markdown, so:

- the **TipTap ↔ Markdown round trip** (§4) is untouched — there is no
  property macro to serialize, no node type to lose on a paste, and no way
  for an editor bug to corrupt a property;
- the **converted-markdown corpus** and the chunker/embedding pipeline (§9)
  see exactly what they saw before — properties are not indexed as prose and
  do not perturb a page's chunk boundaries;
- the **CRDT co-editing document** (§8's edit sessions) stays the page body
  alone; two people editing properties are editing rows, not a shared text
  buffer;
- the **Confluence importer** (§13) is unchanged and out of scope. Confluence
  stores properties as a macro inside content, and mapping that into this
  model is a separate decision with its own fidelity questions.

The cost of that separation is stated plainly: a property is invisible to
full-text and semantic search, and a page exported as Markdown loses its
properties. Both follow directly from "properties are not content" and are
the right trade for keeping the content pipeline uncomplicated.

### 20.1 Keys come from a registry, not from the author

Instance admins define the allowed keys; page editors pick one and supply a
value. Free-form keys were considered and rejected, and the reason is
concrete rather than aesthetic: with free-form keys nothing stops `Owner`,
`owner`, and `Owner ` from all existing, and the moment anyone wants to
*report* over properties (§20.5) those are three different columns with no
way to reconcile them after the fact. A registry makes the vocabulary a
decision someone made once, rather than an accident of whoever typed first.

Uniqueness within the registry is enforced on a **normalized** key —
`Trim()` then `ToLowerInvariant()` — stored in its own `KeyNormalized`
column, which is where the unique index lives. **Not** on the display key,
deliberately: SQL Server's default collation is case-insensitive while
SQLite's is case-sensitive for ASCII, so a unique index on the raw key would
mean `Owner` and `owner` collide in production and coexist in the SQLite test
tier (§14) — the two tiers would be enforcing different rules, and the looser
one is the one that runs on every commit. Normalizing in the application
makes the answer byte-identical on both providers. This is the same instinct
as the BIN2 storage-key column in the SQL Server file-storage provider (§10):
never let a collation default decide a correctness question. Custom emojis
(§19) get the same guarantee for free because their grammar admits lowercase
only; property keys are display strings, so they have to earn it.

Value lengths are likewise checked in the service rather than left to the
column, for the same tier-parity reason: SQLite does not enforce declared
string lengths, so an over-long value would silently store in tests and fail
in production.

### 20.2 Permissions

- **Reading** a page's properties requires only `canView` on that page.
  Properties carry no restriction of their own — exactly the §6.4.2 rule for
  comments and labels — and they are resolved through the same DataLoader
  discipline (§8), on pages that already passed object-level authorization to
  be resolvable at all.
- **Setting or removing** a value requires `canEdit` on **that page**. It is
  an edit of that page's metadata, the same call as attaching a label.
- **Replicas are read-only.** Both value mutations fail with
  `ReadOnlyReplicaError` beneath every grant (§12); on a replica, property
  rows arrive only through sync import.
- **The registry** (create/delete a key) requires instance `admin`, resolved
  by the caller from the token's realm role and passed in — the service never
  derives it, the same shape `ISpaceService`/`ICustomEmojiService` use. The
  key *list* is readable by any authenticated user: it is vocabulary, like
  the emoji registry, and a key's existence says nothing about which pages
  use it.
- **Deleting a key that pages are using is refused**, with a `ValidationError`
  naming how many pages use it. Cascading the delete would silently destroy
  content that nobody chose to delete, which is precisely what §6.4.1's
  "deletion is an explicit, audited operation, never a side effect" exists to
  prevent. The count is safe to report and is deliberately as far as it goes:
  a per-page answer would reveal which restricted pages carry the key (§6.7).

### 20.3 Audit

Four actions, all through the domain-event pipeline (§7), so the audit row
commits in the same transaction as the change:

| Action | Subject | Details |
|---|---|---|
| `page.property.set` | `page` | `{ key, value }` |
| `page.property.remove` | `page` | `{ key }` |
| `property_key.create` | *(none)* | `{ key, propertyKeyId }` |
| `property_key.delete` | *(none)* | `{ key, propertyKeyId }` |

Two judgement calls are worth stating rather than leaving in the code.

**Subject types.** `AuditSubjectType` is a closed list — page, space,
attachment, comment, rule — and no member fits a property or a registry key.
Rather than widen it, value changes are audited against the **page** whose
metadata changed (the same call the label mappings make, and the more
meaningful "what changed" either way), and registry changes carry **no
subject at all** with the key named in the details (the custom-emoji
precedent, §19). The action name carries the distinction in both cases.

**The value is in `DetailsJson`, on purpose.** §15 forbids page content,
search text, and attribute values from traces and logs, and it would be easy
to read that as forbidding them here too. It does not, and the distinction
matters: telemetry is an operational side channel with its own retention and
a wider audience, while the audit table is the regulated record of who did
what, access-controlled like the content it describes. "What did this
property become" *is* the change being recorded — a `page.property.set` row
that did not say what was set would be a log line, not an audit record.
Removal records only the key: the row is gone, and its previous value is
already in that page's earlier `set` row.

### 20.4 Sync

`SyncEventType.PageProperties` (9). Payload:
`{ pageId, key, value, action: "set" | "remove" }`.

The payload carries the key's **name, never this instance's registry row id**.
The registry is instance-local — it is not exported, and there is no space to
journal a key creation against — so the receiving instance may well have
never seen the key, and an id would point at nothing. On import the key is
found-or-created by its **normalized** name before the value is applied,
exactly how a label event finds-or-creates its `Label` row (§12). Both
directions are idempotent: a repeated `set` overwrites with the same value, a
repeated `remove` finds nothing to remove. `pageId` stays top level so the
import's affected-page collection reindexes and notifies the page the same
way a label or comment event does.

A key materialized this way has no local creator and no local sort order, and
both stay empty rather than being invented: `CreatedByUserId` is null (the
same "system action, no user" shape `AuditEvent.UserId` already has), and the
key lands at the front of the display order for a high-side admin to arrange
— presentation order is each instance's own choice, not synced content. The
same applies to a value row's `UpdatedByUserId`: the payload carries no
actor, and a replica is read-only to users anyway, so every property row on
the high side has no local author.

Like labels, properties travel on **incremental** events only; a baseline
bundle carries pages and their revisions. A space baselined after properties
were set will not carry them until the next change to each — the same
documented simplification baselines already have (§16, milestone 6).

### 20.5 Not built yet: cross-page reporting

There is **no** "every page in this space with `Status: Draft`" query, and no
property facet in search. RQL (§22) is now the cross-page query surface, and it
deliberately does **not** include a `property` field: the key registry is
admin-defined, so admitting it would mean opening RQL's closed field set to an
open-ended key space — a vocabulary decision that has not been made rather than
an omission. Everything below still applies on the day it is.
The *data model* supports one without a migration —
value rows are keyed by `(PageId, PagePropertyKeyId)` and indexed
`(PagePropertyKeyId, PageId)`, which is exactly the access path such a report
needs — but the query surface does not exist.

When it is built, it must be permission-filtered **per page**, the way
`GetPagesByLabelAsync` is (§6.4.2): properties are flat, so there is no
prune-the-subtree shortcut, every candidate page needs its own restriction
check against its own ancestor chain, and a page the caller cannot view is
absent entirely — not a redacted row, and not implied by a count (§6.7). The
other thing to get right on that day is the missing query filter: property
rows for a soft-deleted page linger, exactly as `PageLabel` rows do, so a
report must join to `Pages` rather than assume every row belongs to a live
page.

---

## 21. Protective markings

Every page carries a **protective marking**: a UK Government classification, an
optional *eyes-only* caveat naming the countries the page is releasable to, and
an optional national *prefix* — together, `UK SECRET [UK/US EYES ONLY]`.

The first two gate access. The prefix does not, at all, ever (§21.12).

**It enforces.** A marking is not a banner the author draws and the reader
respects; it gates who can view the page, on every read path, exactly as a page
restriction does. That is the whole reason it exists, and every other decision
in this section follows from it.

### 21.1 The scheme

Fixed, ordered, and not configurable:

```
OFFICIAL  <  OFFICIAL_SENSITIVE  <  SECRET  <  TOP_SECRET
```

The ordering **is** the comparison — a principal may view a page when their
clearance is at or above the page's level — so the four values are hard-coded as
a `ClassificationLevel` enum with load-bearing numeric values (`tinyint` in the
database, so the comparison is numeric on every provider). The scheme is set by
policy, not by an admin, which is precisely what makes hard-coding it safe: there
is no fifth level to insert, and a member inserted in the middle would silently
re-rank everything below it. Numbering starts at **1**, so
`default(ClassificationLevel)` is not a valid level and an uninitialized value
can never read as OFFICIAL.

Three spellings of a level exist and they are deliberately different things: the
**wire name** (`OFFICIAL_SENSITIVE`) is what the `clearance` claim, the sync
payload, the audit `DetailsJson` and the GraphQL enum all use; the **display
name** (`OFFICIAL-SENSITIVE`, `TOP SECRET`) is the UK Government's own written
form and appears only in the rendered marking; the **reason token**
(`official_sensitive`) appears only in denial reasons. One method each, in
`ProtectiveMarking`, so they cannot drift.

"So they cannot drift" is only true if clients can *get* the display name, and
two surfaces need a level's spelling on its own rather than a whole marking — a
one-word list badge, and the picker that offers a level before one is chosen. Both
are served from the server, from the same `ProtectiveMarking.LevelName`:

- `PageMarkingView.levelName` — the level of a marking the caller already holds.
  Note it is **not** interchangeable with `label`: `label` is the whole marking
  (prefix, level, caveat) and is what anything claiming to show "this page's
  marking" must render. Rendering `levelName` in its place drops the caveat, which
  understates the marking. That is the safer direction of error — the caveat is
  still *enforced*, the badge is informational — but it is still wrong, which is
  why the two fields are named to be hard to confuse.
- `Query.classificationScheme` — every level in **scheme order** with its display
  name, for the picker, which has no marking in hand. The list order is the scheme
  order, so a client never encodes that OFFICIAL sorts below SECRET. Deliberately
  no numeric rank: the only use for one is comparing levels client-side, and the
  comparisons that matter (may I read this, may I set this) are decisions the
  server already makes and returns typed errors for.

Without those two fields a client hard-codes four spellings and their order, which
is exactly the second implementation this paragraph exists to forbid — and the
order it would duplicate is the access comparison itself.

### 21.2 It composes by subtraction, and only by subtraction

Effective view access becomes:

```
canView(page) = spaceRole ≥ viewer
                AND every view-restriction on page + ancestors passes
                AND clearanceAllows(marking, principal)
```

**Nothing grants around it.** A space grant, a passing page restriction, a
`space-admin` role, the instance `admin` role — none of them widens a marking, in
the same way and for the same reason none of them reads around a page restriction
(§6.5). This is structural rather than remembered: the check lives inside
`EffectivePermissionCalculator.Compute`, the one computation every read path
already funnels through, and there is no parameter, overload, or flag by which a
caller can obtain a `canView` that skipped it. `canEdit` is reached only by
falling through `canView`, so an editor who fails clearance loses edit too
without the gate knowing edit exists.

The single loader in the data layer (`PermissionContextLoader`, §6.7) supplies
the marking alongside the grants and restriction chain, so a call site cannot
supply "no marking" any more than it can supply "no restrictions". Its batched
path loads every marking **and its country rows** in one query for the whole
batch — search post-filters hundreds of candidates, and an N+1 sitting on the hot
path of the control itself would be the thing that gets the control turned off.

Ordering inside the computation — space role, then classification, then the
restriction chain — affects only which reason a denial *reports*, never the
verdict: both gates are conjuncts. Clearance is reported ahead of a failing
restriction because "this principal has no business reading this page at all" is
the more actionable answer for a reviewer, and because it is the cheaper check,
so a page the caller cannot be cleared for costs no rule evaluations.

### 21.3 Clearance, and every fail-closed choice in it

Clearance is an ordinary **principal attribute** (§6.1/§6.2) under the well-known
key `clearance`, resolved per request from the token, never from the local `User`
mirror. Expected claim values are the four wire names. Being an ordinary
registered attribute is the point: it inherits §6.1's "evaluate the token" rule
for free rather than needing its own plumbing.

- **Absent, unrecognised, or malformed clearance grants OFFICIAL — and only
  OFFICIAL.** This is a deliberate middle, not a compromise. "No clearance = see
  everything" is obviously wrong. "No clearance = see nothing" is wrong in a
  subtler way: an unconfigured claim mapper would empty the entire wiki for every
  user, which is an outage dressed as security and, worse, an outage that
  pressures whoever is on call into turning the check off. Granting the least
  sensitive tier denies everything the control exists to deny while leaving
  OFFICIAL content readable exactly as it was before markings existed. It matches
  the engine's existing doctrine that a missing attribute matches no condition
  (`Attr_MissingAttribute_FailsClosed`): the principal gets nothing from the
  attribute, and OFFICIAL is what nothing is worth.
- **Parsing is closed and ordinal.** Only the four wire names parse.
  `Enum.TryParse` is deliberately not used: it accepts the C# member spellings,
  can be made case-insensitive, and — the reason it is disqualified — happily
  parses `"4"` into `TOP_SECRET`, so a numeric claim value would grant the top of
  the ladder.
- **A multi-valued clearance claim takes the highest recognised value**, mirroring
  §6.4's "your role is the highest whose expression you satisfy". Unrecognised
  values are ignored rather than poisoning the result, so garbage can never raise
  clearance and can never lower it below the OFFICIAL floor.
- **A stored level outside the ladder becomes TOP SECRET.** `Level` is a tinyint,
  so a hand-edited row, a botched restore or a future migration bug can present a
  value the enum does not define, and the two failure directions are not
  symmetric: a value *above* the ladder denies everyone (noisy but harmless),
  while `0` — what an uninitialized tinyint is — compares as less than every
  clearance and would make the page readable by **everybody**. That is a silent
  bypass of the entire control, so `ProtectiveMarking.Create` normalizes an
  undefined level at the one constructor rather than trusting it at each
  comparison. Relatedly, the level-naming methods answer TOP SECRET for an
  unknown value instead of throwing: an exception on a read path is a 500, a 500
  is distinguishable from a not-found, and that is exactly the §6.7 leak the
  denial design exists to prevent.

  **The SPA does not match this exactly, on purpose — change both together.** Its
  affordance ladder (`web/src/markings/clearance.ts`) ranks an unknown level
  *above* TOP SECRET rather than normalizing it *to* TOP SECRET, so a TOP SECRET
  principal is offered the page here and not there. The gap is only reachable when
  the server knows a level the client does not, and on that path the affordance
  should be the stricter side: a greyed-out level the server would have allowed
  costs a click, while offering one it refuses is the failure the affordance
  exists to prevent. Both sides pin their own direction by test. If this
  normalization is ever revisited, revisit that one in the same change — the two
  are deliberately a notch apart, which is exactly the kind of difference someone
  later "fixes" into agreement without knowing it was chosen.

### 21.4 The eyes-only caveat

A marking may carry a **set of countries**; a principal must hold at least one
`nationality` value in that set. An empty set means no caveat. Absent or empty
nationality **denies** any page carrying one — fail closed, consistent with
`AttrCondition`: a principal with no value for an attribute matches no condition
that tests it, and the caveat is a condition on nationality.

**The vocabulary is the nationality attribute's, not ISO 3166.** A country value
is valid only if it appears in the registered `nationality` attribute's
`AllowedValuesJson` (§6.2). This is the single most important detail in the
caveat, and it is not fussiness: enforcement compares the marking's set against
the principal's `nationality` claim values, which are whatever this instance's
Keycloak mapper emits — `GB`, `UK`, `GBR`, something site-specific. Populating
the marking side from a hard-coded ISO list would let an admin pick `GB` on a
wiki whose tokens say `UK`, and every comparison would fail. The failure mode is
the dangerous kind: it fails *closed*, so nothing looks broken — the page simply
becomes invisible to everybody, including the audience it names, while the
marking reads as perfectly correct in the admin UI. Drawing both sides from one
registry makes that class of mismatch unrepresentable.

The consequence is accepted rather than worked around: **if no `nationality`
attribute is registered, or it declares no allowed values, an eyes-only set
cannot be set at all.** There is nothing to pick from, and a caveat naming values
the instance does not recognise would match nobody. The refusal is a
`ValidationError` naming the attribute. A level-only marking still works fine on
such an instance.

**Canonical form.** Country values are stored and compared **upper-cased,
trimmed, de-duplicated and ordinally sorted**. Canonicalizing in one place means
the stored rows, the display string, the audit `DetailsJson` and the sync payload
all agree byte-for-byte, and a set that round-trips through sync comes back
identical rather than merely equivalent.

That upper-casing is a **documented, deliberate departure from §6.3's "matching
is exact (ordinal), no case folding"**, confined to this one comparison. §6.3
keeps rule matching ordinal because an admin hand-typing a group name should not
have a typo silently forgiven. Here the two sides come from different systems
that were never guaranteed to agree on case — an admin-registered vocabulary and
an OIDC claim mapper — and a case mismatch would deny every legitimate reader
while looking correct. Failing closed on a casing difference is not security, it
is an outage. The rule engine's `attr` conditions are untouched.

**Rendering** has exactly one implementation, server-side
(`ProtectiveMarking.Format`, exposed as `PageMarkingView.label` in GraphQL), so
the SPA, an MCP client and an audit reviewer all read identical text. Two
renderings of one marking that disagree is a compliance problem, not a cosmetic
one. The format is the level, then the caveat in brackets:
`SECRET [UK EYES ONLY]`, or `SECRET [UK/US EYES ONLY]` for several countries in
canonical order. The country tokens are the instance's own registered values
verbatim — an instance that registered `GB` renders `[GB EYES ONLY]`. Aliasing
`GB` to `UK` for display was considered and rejected: a marking must read back as
the thing that is actually enforced, and a display-only alias is how "we thought
it said UK" happens.

### 21.5 Every page is marked

There is **no unmarked state**. A page's marking is created with the page,
inheriting its parent's (root pages start at OFFICIAL), and an editor may
override it afterwards **in either direction**.

Inheritance happens **once, at creation**, producing a value the page then owns —
it is not re-derived from ancestors at read time the way restrictions accumulate
(§6.4). So a child may legitimately sit above *or below* its parent, and an
editor changing a parent's marking does not silently re-mark the subtree. The one
place that asymmetry shows is the page tree, which prunes a node the caller
cannot be cleared for **along with its whole subtree**, including children the
caller *could* see: a tree cannot render a node whose parent is absent, and the
more-hidden direction is the safe one. Such a child stays reachable by id and
through search, both of which check it on its own.

The invariant is enforced at the **persistence seam**: `RocketWikiDbContext`
materializes an OFFICIAL marking for any `Page` being inserted without one, so an
unmarked page cannot be committed through any code path — present or future —
that goes through the context. This is the write-side twin of putting the
clearance gate inside the calculator: an invariant that lives in one structural
place cannot be lost one call site at a time. It is a backstop, not the feature —
`PageService` sets the marking explicitly with real parent inheritance, and the
sync importer sets it explicitly too — and it touches the database not at all, so
it costs nothing on every write.

**A page found at read time with no marking row is treated as TOP SECRET.** Belt
and braces against a future code path that forgets, and the substitution has
exactly two implementations (`PermissionContextLoader` and its batch sibling) so
no consumer ever holds a nullable marking it could decide to ignore. Note the
asymmetry with the insert-time default, which is intentional: a missing marking
on *read* means something went wrong, and the answer to that is the top of the
scheme; a missing marking on *insert* means nobody said, and defaulting that to
TOP SECRET would classify content nobody asked to classify and lock its own
author out of it.

### 21.6 Changing a marking

`setPageMarking` replaces the whole marking — level and country set together. A
marking is one value; a partial update would let a caller change the level
without ever stating what caveat they meant.

- Requires **`canEdit` on that page**, beneath the replica invariant (§12), which
  refuses first and beneath every grant. `canEdit` already includes the clearance
  gate against the page's *current* marking, so a page you cannot see is a page
  you cannot re-mark — including re-marking it downward to make it readable.
- **You may not set a marking you could not then read.** Enforced as the
  resulting marking *as a whole* rather than just its level, because that is what
  mechanizes the stated reason: marking a page `SECRET [US EYES ONLY]` as a
  GB-national editor loses you the page just as completely as over-classifying it
  does. Refused as a `ForbiddenError` — the input is well-formed, the caller is
  simply not entitled to the result.

**The UI prevents rather than refuses, and that is affordance data, not
authorization.** `me.clearance` and `me.nationality` echo the caller's own
resolved attributes so the marking picker can grey out a level above their
clearance, and warn about an eyes-only set that excludes their own nationality,
instead of offering a choice the server will reject. Three properties make that
safe rather than a second access-control implementation:

- **It is the caller's own token, echoed back.** Same category as `groups`, which
  `me` already returned; it discloses nothing the caller did not present.
- **Both fields resolve through `ClearanceGate`, not the raw claim** — the same
  path enforcement uses. `ResolveClearance` so a garbage claim reads as OFFICIAL
  here exactly as it does at the gate, and `ResolveNationalities` so the values
  are *canonicalized*. That second one is load-bearing: a marking's country set is
  always canonical, so a token saying `gb` is admitted to a `GB` marking by the
  server, and a client comparing against the raw claim would have concluded the
  opposite and warned the author out of a marking that would have worked. It is
  §21.4's case-mismatch trap one layer up, closed the same way — one
  canonicalizer, both sides.
- **The server decides regardless.** `PageMarkingService` re-checks the resulting
  marking against the caller's clearance and returns a typed error; a stale,
  spoofed, or simply wrong client-side comparison changes nothing but the polish.

**Downgrading is permitted but audited distinctly.** A change is a *downgrade*
when it makes the page readable by someone it was not readable by before: the
level drops, the caveat is cleared, or the caveat gains a country it did not
admit. Swapping `{GB}` for `{US}` counts, even though GB also loses access —
somebody who could not read the page yesterday can read it today, which is the
fact a reviewer is looking for. Erring toward "call it a downgrade" is the safe
error: the cost is one extra row in a reviewer's result set, and the cost of the
opposite error is a widening nobody sees.

### 21.7 Audit

Two actions, one domain event, through the pipeline (§7) so the row commits in
the same transaction as the change:

| Action | Subject | Details |
|---|---|---|
| `page.marking.set` | `page` | `{ level, eyesOnly, prefix, previousLevel, previousEyesOnly, previousPrefix }` |
| `page.marking.downgrade` | `page` | same shape — emitted instead of `.set` when the change widens the audience |

Deriving the action from the event (rather than minting two event types with
identical payloads) keeps "what counts as a downgrade" in exactly one place. The
GraphQL field declares `page.marking.set` for the coverage guard and for denial
rows; a denial has no before/after, so `set` is the honest name for it.

Subject is the **page**, the same judgement call the label and page-property
mappings make: `AuditSubjectType` is a closed list with no member for a marking,
and the page whose classification changed is the meaningful "what changed"
anyway.

The before-state is not decoration. Markings are a single mutable row, so — as
with access rules (§7) — **the audit log is the only history there is**, and a
reviewer asking "what was it before this was relaxed" has no other source. This
is also the only place the country set is written out in full: §15 keeps it out
of every telemetry tag, and the denial reason deliberately names no country.

A page's *creation* raises no marking event: the inherited value is deterministic
from the parent, which the `page.create` row already identifies, and a
`page.marking.set` row beside every `page.create` would be noise that made real
marking changes harder to find.

### 21.8 Denial is invisible (§6.7)

A page the caller lacks clearance for is **absent, not forbidden** — identical in
every respect to a page that does not exist, byte-for-byte at the HTTP boundary,
through every path a `Page` is reachable by. The audit row records the real
reason; the caller cannot tell.

Denial reasons follow the existing vocabulary:

| Reason | Meaning |
|---|---|
| `classification:{level}` | clearance below the page's level, e.g. `classification:top_secret` |
| `caveat:eyes_only` | the eyes-only set and the principal's nationalities do not intersect |

The level is checked before the caveat, so one denial names one reason and a
caller who lacks the level is not told (via the audit row) that they also lack
the nationality. **The caveat reason names no country**, deliberately: the
specific set belongs in the audit row, and a reason string carrying the countries
would put the marking's contents one careless tag away from a metric dimension.

**Telemetry (§15).** `CoreTelemetry.CategorizeDenialReason` collapses
`classification:{level}` to `classification` and `caveat:eyes_only` to `caveat`.
Keeping the level would be tempting — it is a bounded four-value tag — and is
dropped on purpose: a "denials by classification level" time series is a census
of how much SECRET and TOP SECRET content exists and how hard it is being probed,
published to whatever audience the dashboard has. That is exactly the second,
unregulated record of who-reads-what §15 exists to prevent, and the audit table
already holds the specific level for anyone entitled to ask.

### 21.9 Reach

Enforcement is inherited, not reimplemented, everywhere `canView` is already
computed through `PermissionContextLoader`: page reads, `parent`/`children`,
revision history, search (keyword and vector — the post-filter is the same
batch), Ask-the-wiki retrieval (a page above the asker's clearance never enters
the prompt, not merely the citation list), MCP tools, attachments, comments,
labels, page properties, watch state, the notification read model, the co-editing
hub's join check, and the §6.6 permission inspector (whose non-short-circuiting
`Explain` mirrors the gate's classification verdict exactly, pinned by test).

Two read paths assemble their own authorization inputs and therefore had to have
the gate added by hand. Both now have it; both are worth knowing about:

- **`PageReadService.GetPageTreeAsync`** walks a whole space in memory precisely
  to avoid per-node work, so it hand-rolls the restriction evaluation the loader
  would otherwise order for it. The marking is loaded on the same constant-query
  budget — one more query for the whole space.

  Each surviving node also **carries its marking out** on `PageTreeNode.Marking`
  (`marking` in GraphQL), so the tree — the one listing surface with no other
  route to a `Page` — can render a classification badge without a query per node.
  It is the *same value the walk gated on*, passed along rather than re-loaded:
  a second lookup could in principle read a different row than pruning consulted,
  and a tree that displayed a marking other than the one it enforced would be
  exactly the wrong kind of wrong. Leak-safe on the same construction as
  `OwnViewRestrictions` — the node exists only because the caller passed the gate
  for that marking, so showing it is showing them why they were let in. The MCP
  `get_page_tree` payload maps through its own DTO and picks fields explicitly, so
  the Core record growing did not grow a published tool contract by accident — it
  now carries the marking because §21.13 decided it should, deliberately and with
  its own tests, reusing *this same carried value* at no extra query.
- **`INotificationDispatcher`'s fan-out** lives in `RocketWiki.Api`, and
  `PermissionContextLoader` is internal to `RocketWiki.Data`, so it cannot use
  the loader at all. It loads the page's marking once per fan-out.

### 21.10 Sync (§12)

Markings **travel with content**. A page that is SECRET on low is SECRET wherever
it lands; letting the high side rediscover that for itself is the hole this
closes. This is unlike space grants, which stay local because the high side
decides who may read its replica — a grant is about *this instance's* people, a
marking is a property *of the content*.

- `SyncEventType.PageMarking` (10), payload `{ pageId, level, eyesOnly, prefix }`
  — the level as its **wire name**, never the tinyint, so a future renumbering
  cannot silently re-rank a bundle already sitting on a transfer disk. The prefix
  travels even though it gates nothing (§21.12): a replica must render the same
  marking string as its origin. An **absent** `prefix` key means *no prefix*,
  never this instance's default.
- Every `PageUpsert` **also** carries the page's current marking, attached at
  export time (like revision history, and for the same two reasons: journal
  payloads stay lean, and outbox rows written before §21 existed still export with
  a marking). Baselines carry it too, which is the gap that would otherwise matter
  most — a space baselined after markings were applied would deliver its whole
  back catalogue unmarked. The redundancy with the dedicated event is deliberate:
  both apply idempotently to the same row, so an overlap is harmless and a gap
  would not be.
- Only the **after** state crosses. Sync replays state; the before/after pair
  exists for the low side's reviewer, in the low side's audit table, which never
  crosses.
- The country set crosses verbatim. The receiving instance may have no matching
  nationality vocabulary, and that is handled the §12 way rather than by dropping
  the caveat: an unrecognised country matches no principal, so the page arrives
  **more** restricted — exactly as "a group/attribute unknown on high matches
  nobody" already works for restrictions. Dropping it would be the one unsafe
  direction.
- **A page cannot land on the high side unmarked.** A payload with no marking and
  no local row creates the row at **TOP SECRET**; a payload with no marking for a
  page the high side already holds a marking for leaves it alone (silence must
  never re-classify, in either direction). An unparseable level reads as absent,
  not as OFFICIAL.
- Marking rows applied by import have no `SetByUserId` — the payload carries no
  actor, and a replica is read-only to users anyway.

### 21.11 The OFFICIAL backfill, and its risk

The `AddPageMarkings` migration stamps every pre-existing page **OFFICIAL**, the
lowest level in the scheme, with no actor.

**This is the pragmatic call, not the safe-by-default one, and it is not
disguised as one.** Nobody has reviewed that content; the wiki is asserting
OFFICIAL on its behalf because the alternative is worse. If any pre-existing page
is in fact SECRET or above, it is now readable by exactly the people who could
already read it — but it is *wearing a marking that says it is fine*. That is the
real risk: the marking looks like a reviewed judgement and is not one.

**Existing content must be reviewed and re-marked.** `SetByUserId IS NULL` on
`PageMarkings` is the query that finds every page nobody has yet looked at.

Backfilling to TOP SECRET was considered and rejected once, with reasons: it
would make every page invisible to everyone below TOP SECRET the moment the
migration ran — the wiki would lock itself out of itself, including out of the
admin pages describing how to fix it, and including on instances where nobody has
a clearance claim configured at all (who resolve to OFFICIAL by §21.3's default).
The recovery would be a hand-written `UPDATE` against a production database,
which is the one operation this whole design exists to avoid. An access control
that has to be switched off to be adopted does not get adopted.

### 21.12 The national prefix

UK protective markings are conventionally written with a national qualifier —
`UK OFFICIAL`, `UK SECRET`, `UK TOP SECRET` — so a marking carries an optional
**prefix** alongside its level and caveat.

**It is presentational, and that is a hard boundary, not a phase.** The prefix
has *no access-control considerations whatsoever*:

- `ClearanceGate` does not read it. The gate's entire input is the level and the
  eyes-only set; `ProtectiveMarking.Prefix` is never touched by it. (The proof is
  mechanical: the commit that introduced the prefix has a **zero-line diff** on
  `ClearanceGate.cs` and on `EffectivePermissionCalculator.cs`.)
- It never appears in a denial reason. `classification:{level}` and
  `caveat:eyes_only` are unchanged, so nothing about a prefix can reach an audit
  reason or — via `CategorizeDenialReason` — a metric tag.
- It changes no verdict. Pinned by test at two tiers: `ClearanceGateTests` sweeps
  every level × caveat × principal combination and asserts the decision **and the
  reason** are byte-identical with and without a prefix, and `PageMarkingTests`
  repeats it through the real permission loader against a real database.
- It is outside "you may not set a marking above your own clearance" (§21.6), and
  it falls outside *for free* rather than by exception: that rule is
  `ClearanceGate.Check(resultingMarking, principal)`, and the gate does not read
  the prefix, so there is no prefix a caller can be refused for.

If you are reading this because you were about to give the prefix access
semantics "for completeness": don't. There is nothing to compare it against. A
principal has no "national prefix" claim, and inventing one would silently
duplicate the nationality attribute the eyes-only caveat already uses — with
different values, a different vocabulary, and no registry behind it. Note also
that the prefix and the caveat countries are independent: `UK SECRET [US EYES
ONLY]` is an ordinary marking, and reading the leading `UK` as a releasability
statement would be exactly backwards.

**It defaults to `UK`.** New markings, markings inherited from a parent, and —
via the `AddPageMarkingPrefix` migration — every row that predated the feature.
An instance whose content is not UK-marked changes the value per page; the
default is a default, not a policy.

**`NULL`/empty is legal and must stay clearable.** Some content legitimately
carries no national qualifier, so the column is nullable, the mutation accepts
null, and the normalizer collapses null, `""` and whitespace to the same "no
prefix" state. That state renders the bare level with **no leading space** —
`SECRET`, not ` SECRET` — because a cosmetic gap would make two identical
markings compare unequal as strings, and the label is what the SPA, an MCP client
and an audit reviewer all read.

**Normalized on write**, trimmed and upper-cased, exactly like the country
values and for the same reason: the stored row, the label, the audit
`DetailsJson` and the sync payload must agree byte-for-byte. `uk` in becomes
`UK` stored.

**The label is the one place it appears.** `ProtectiveMarking.Format()` — exposed
as `marking.label` — renders prefix, space, level, then caveat. All four
combinations:

| Prefix | Caveat | Label |
|---|---|---|
| `UK` | `{UK, US}` | `UK SECRET [UK/US EYES ONLY]` |
| `UK` | none | `UK SECRET` |
| none | `{UK, US}` | `SECRET [UK/US EYES ONLY]` |
| none | none | `SECRET` |

There is deliberately **no second formatter**. The frontend consumes `label`
rather than composing prefix + level itself, for the same reason it did before
the prefix existed: two renderings of one marking that disagree is a compliance
problem, not a cosmetic one.

**Where else it goes, and where it does not.** It travels with the marking
through sync (`prefix` on both the `PageMarking` event and the `marking` object
on a `PageUpsert`) — "presentational" is exactly *why* it must cross, since a
replica showing a different marking string from its origin on identical content
is precisely the confusion this avoids. An **absent** `prefix` key on an inbound
payload means *no prefix*, never "apply this instance's default": a pre-prefix
bundle said nothing about a national qualifier, and defaulting one in would
assert something its origin never said. It is recorded in the audit
`DetailsJson` as `prefix`/`previousPrefix`, because a prefix change *is* a change
to the marking and a reviewer must be able to explain why a page's rendered
marking changed. It reaches **no** telemetry tag.

**It is not a downgrade.** `ProtectiveMarking.IsDowngrade` does not consult it:
a downgrade means somebody who could not read the page yesterday can read it
today, and the prefix cannot move that line in either direction. Clearing
`UK SECRET` to `SECRET` audits as an ordinary `page.marking.set`, with the
before-and-after prefix in the details. Counting it as a downgrade would dilute
the one query that exists to find real widenings.

**`ProtectiveMarking.FailClosed` carries no prefix**, unlike `Baseline`. That
value means "this page's marking row is missing and we do not know what it
said", so asserting a national qualifier on its behalf would be inventing a
fact. It renders a bare `TOP SECRET`, which is also a quiet visual signal that
something is wrong — every marking the application actually writes carries one.

**The migration is a second one** (`AddPageMarkingPrefix`), not an edit to
`AddPageMarkings`. That one is already applied on real SQL Server, and editing an
applied migration produces a schema that can never be reproduced from zero.
Unlike the OFFICIAL backfill (§21.11) this one carries **no security risk and
needs no review sweep**: the prefix grants nothing and denies nothing, so
asserting `UK` on a page nobody has reviewed cannot change who can read it.

### 21.13 Aggregation: what a compilation of marked content is marked

A search result list, an MCP payload and — above all — an Ask-the-wiki answer are
**compilations**. Standard doctrine: a compilation carries the classification of
its most sensitive constituent. Before this, an answer synthesized from a
`UK SECRET` page arrived with no marking at all, and a cleared reader could
legitimately paste it somewhere that was not. The model launders the marking off
the content; this puts it back on. Two rules, together:

1. **Every individual result carries its own marking** — search hits, MCP items,
   Ask citations.
2. **The containing result carries the aggregate**: the highest classification
   among everything that fed it.

**A display label, not a marking, and not enforcement.** This is the distinction
the whole subsection rests on. `AggregateMarkingLabel` is deliberately not a
`ProtectiveMarking`, is never stored, and gates nothing — *enforcement already
happened, per source, before it was computed*. Retrieval and search run under the
caller's own principal, so every contributing page individually passed `canView`
and the clearance gate; a page the caller cannot see contributes nothing because
it never reached retrieval (§6.7), which is verified by test rather than assumed.
The aggregate exists to tell a human what the text in front of them *is*.

That is structural rather than promised: the type lives in **`RocketWiki.Api`**,
and neither `RocketWiki.Core` (where `ClearanceGate` and
`EffectivePermissionCalculator` live) nor `RocketWiki.Data` (where every gated
read service lives) references `RocketWiki.Api`. No enforcement code *can* consult
it — the same argument that keeps `PermissionContextLoader` internal to
`RocketWiki.Data`. It also takes no `Principal` and returns no verdict type, both
pinned by reflection test.

**The aggregate covers everything that entered the model context, not merely what
got cited.** Retrieved content that shaped an answer without earning a citation
shaped it anyway, and a marking a model could defeat by declining to cite would
not be a marking. So `AskWikiService` aggregates over exactly the set the
`assistant.ask` audit row reports as `retrievedPageIds` — the pages whose chunks
actually reached the prompt (a page whose chunks did not fit the char budget never
reached it and must not raise the label). The test that matters plants a SECRET
page in the context, scripts the model to cite *nothing*, and requires the answer
to come back `UK SECRET`.

**Level** is the maximum over contributors. That part is easy, because levels are
totally ordered.

**Caveat is a truthful conjunction, and this is the subtle part.** The storage
model holds one eyes-only set per page; an aggregate can have sources with
different ones, and there is no honest single set:

- The **union** — `[GB/US EYES ONLY]` — says either nationality suffices. That is
  a widening and it is false: the GB source is still GB-only.
- The **intersection** is worse, and it is why this paragraph exists. `{GB} ∩ {US}`
  is **empty**, and an empty eyes-only set in this model means *no caveat at all* —
  so the two most restrictive inputs available would produce the least restrictive
  possible output, silently, while the label looked perfectly correct. That is the
  level-0 trap of §21.3 wearing a different hat: a value that reads as "nothing
  here" when it should read as "everything here".

So distinct source sets are **listed**: `UK SECRET [GB EYES ONLY] [US EYES ONLY]`,
meaning a reader needs both. Identical sets collapse to one entry; a source with no
caveat contributes none; the list order is derived from the sets themselves, so
retrieval rank cannot change the rendered bytes. The invariant that follows is the
one to defend: **an empty aggregate caveat means, and can only mean, "no source had
one"** — it is built by filtering for sources that *have* a caveat, so nothing
subtracts and no set-algebra result can reach empty. Swept by test over every subset
of a mixed corpus.

**Prefix carries through only on unanimity** — including unanimous absence — and any
disagreement (`UK` vs `US`, or `UK` vs none) drops to no prefix, rendering the bare
level. The prefix gates nothing (§21.12), so its only failure mode is
misrepresentation: picking a winner would assert a national qualifier no single
source asserted, and would make the label depend on retrieval order, which is not a
property of the content. Dropping it is the answer `ProtectiveMarking.FailClosed`
already gives for "we do not know what this said". A missing marking row therefore
propagates twice over — TOP SECRET *and* prefixless — which is the right visible
signal that something is wrong.

**No sources yields no label at all**, not OFFICIAL and not TOP SECRET: nothing was
shown, so there is nothing to mark. OFFICIAL would assert a reviewed judgement about
content that does not exist (§21.11's specific complaint), TOP SECRET would invent a
fact, and fail-closed does not apply because nothing is being closed — this decides
nothing. The GraphQL fields are nullable to carry that, and an Ask answer's aggregate
is non-null exactly when its `answer` is.

**One formatter.** The label is rendered by `ProtectiveMarking.FormatLabel`, which
`ProtectiveMarking.Format` (a page's `marking.label`) is now the one-set special case
of. A client renders `label` verbatim and composes nothing — §21.1's rule, and the
reason the aggregate can never render a caveat a hair differently from a page's.

**Where it applies.**

| Surface | Per-item marking | Aggregate |
|---|---|---|
| `askWiki` | `citations[].marking` (the source page's own) | `aggregateMarking` over everything that entered the context |
| `search` | already reachable as `edges[].node.page.marking` — no field was added, because a hit reaches its page through the object-level-authorized `page` resolver and a projected copy would route around it (§6.7) | `aggregateMarking` over the permission-filtered hit set the connection reports |
| MCP `get_page` | `marking` | — |
| MCP `search` | `hits[].marking` | `aggregateMarking` |
| MCP `get_page_tree` | `pages[].marking`, at every depth | `aggregateMarking` over the whole pruned tree |
| MCP `list_spaces` | — | — (a space is not marked; its pages are) |

The search aggregate spans the whole permission-filtered hit set — the same set
`totalCount` counts — rather than the twenty edges of one page, so every page of the
connection reports the same label. A per-page aggregate would force the SPA to
*combine* aggregates as the user loads more, which is the caveat-conjunction rule
reimplemented in TypeScript, which §21.1 forbids; and a label that drops as you
scroll would tell a user the results got less sensitive when all that happened is
that they paged past the sensitive ones.

**The MCP addition is a published tool contract change, made on purpose.** MCP
clients are typically LLMs, and an unmarked payload is precisely how classified text
ends up summarised into an unclassified context. The payload fields are rendered
label strings rather than structured parts, for that audience: the marking has to
travel with the text, as text, and a client reassembling parts would be the second
renderer §21.1 forbids.

**Telemetry (§15): an aggregate reaches no span, no metric tag, no log.** It is built
from levels, country sets and prefixes — the three things §21.7 confines to the audit
table and §21.8 keeps out of every dimension, because a level in a metric tag is a
census of the classified estate. `AssistantTelemetryHygieneTests` plants a sentinel
country and a sentinel prefix, asserts they really do appear in the returned label,
and then sweeps every ActivitySource and RocketWiki meter in the process for them.

**Audit (§7).** The `assistant.ask` row's details gain `aggregateMarking` — the label
as it was when the answer left. It is not recoverable later from `retrievedPageIds`:
a marking is a single mutable row, so re-deriving it after a re-marking would report
today's classification for yesterday's answer, which is exactly why §21.7 records a
marking change's before-state. The audit table is also the one place a full country
set is allowed to appear.

### 21.14 Deliberately not done

- **No create-time marking override.** A page is created with its inherited
  marking and re-marked afterwards. Stated cost: for a *root* page holding
  classified content there is a brief window at OFFICIAL between creation and the
  first `setPageMarking`. The mitigation is procedural — create the page empty,
  mark it, then write — and the alternative (a marking on `CreatePageRequest`)
  would duplicate the vocabulary validation, the clearance constraint and the
  audit decision into the create path for a window a UI can close.
- **No caveat registry.** One caveat kind, eyes-only, with a country set. A
  general caveat registry (types, per-type semantics, per-type enforcement) is not
  built and is not implied by this design.
- **No "which pages are marked X" report.** The data model supports one without a
  migration — `PageMarkings` is indexed on `Level` and `PageMarkingCountries` on
  `(CountryValue, PageId)`, which is exactly the access path — but the query
  surface does not exist. When it is built it must be permission-filtered **per
  page** the way `GetPagesByLabelAsync` is (§6.4.2): a page the caller cannot view
  is absent entirely, not a redacted row and not implied by a count (§6.7).
- **Markings do not re-mark a subtree.** Changing a parent's marking leaves its
  children exactly as they are; there is no cascade and no bulk re-mark tool.
- **Clearance is not managed in RocketWiki.** Like every other attribute, it lives
  in Keycloak (§6.2). RocketWiki declares that it exists and reads it.
- **No declassification schedule, no review dates, no marking expiry.**
- **No UI.** This is the backend: schema, enforcement, audit, sync and the
  `setPageMarking` mutation. Rendering the marking banner and the editor's
  marking control is the frontend's own piece of work.
- **An aggregate marking (§21.13) is never stored, never enforced, and never
  written back to a page.** It is computed per response from the sources of that
  response. There is no "mark this answer" action, no aggregate on a `Page`, and no
  path by which an aggregate becomes a page's marking — a compilation's label is a
  fact about one response, not a classification decision anybody made.
- **No aggregate on the GraphQL page tree.** `pageTree` nodes carry their own
  markings (§21.9) and the SPA renders a badge per node; there is no
  results-view-style banner over a tree, so there is nothing for an aggregate to
  label. The MCP tree has one because its consumer is a model reading the whole
  payload as text.
- **No aggregate over a whole space, label listing, or export.** Every aggregate
  today spans one response's own items. A "this space is effectively SECRET" figure
  would be the "which pages are marked X" report by another name, with the same
  per-page permission-filtering obligation (see above), and it is not built.

---

## 22. RQL — the page query language

A CQL/JQL-shaped filter language over pages. It ships first behind an embedded
page widget that lists the pages matching a filter, and later behind the search
page's structured filters.

**Not called CQL.** That name is Atlassian's, and reusing it would imply a
compatibility promise this grammar does not keep — the field set is smaller, the
functions are fewer, and the security rules below have no Confluence equivalent.

**The string is the canonical form.** It is what a user types, what a Markdown
fence stores, and what the GraphQL field accepts. There is deliberately **no
structured AST input type**: a client-built tree would need exactly the same
validation the string parser applies, so it would be a second door into the
compiler with a second chance of leaving the closed field set less than closed.
The AST is published as **output** instead (`parseRql`), which is all a
structural query builder needs — it builds, prints through the canonical printer,
stores the string, and reparses it.

### 22.1 Grammar

```
query      := orExpr [ "ORDER" "BY" orderItem ("," orderItem)* ]
orExpr     := andExpr ("OR" andExpr)*
andExpr    := notExpr ("AND" notExpr)*
notExpr    := ["NOT"] primary
primary    := "(" orExpr ")" | predicate
predicate  := field op value
            | field ("IN" | "NOT" "IN") "(" value ("," value)* ")"
            | field "IS" ["NOT"] "EMPTY"
orderItem  := field ["ASC" | "DESC"]
```

Operators: `=`, `!=`, `~` (contains), `!~`, `>`, `>=`, `<`, `<=`.

Keywords (`AND OR NOT IN IS EMPTY ORDER BY ASC DESC`) and field names are
case-insensitive. Values are bare tokens or double-quoted strings with a closed
escape set (`\" \\ \n \r \t`). **Keywords are keywords everywhere**, including
where a value could go, so a value that spells one has to be quoted
(`label = "in"`) — context-sensitive keywords buy nothing and are how a query
language grows corners nobody can reason about.

`NOT` here is not the `NOT` §6.3 forbids. That prohibition is about *access
rules*, where a negation turns an allow-list into a deny-list and a missing
attribute widens access. RQL's `NOT` negates a *content* condition over the
candidate set, and no RQL expression is an input to any permission decision
(§22.4) — it cannot widen anything.

### 22.2 The closed field set

| Field | Type | Operators |
|---|---|---|
| `label` | string | `=` `!=` `IN` `NOT IN` `IS [NOT] EMPTY` |
| `space` | space key | `=` `!=` `IN` `NOT IN` |
| `title` | string | `=` `!=` `~` `!~` |
| `created`, `updated` | date | `=` `!=` `>` `>=` `<` `<=` |
| `creator` | user | `=` `!=` |

Anything else is a validation error naming the allowed fields. Adding a member is
a deliberate widening and belongs in this table before it belongs in code.

- **`creator` is the author of the page's first revision.** There is no
  `CreatedByUserId` column on `Pages` (data-model.md) and RQL did not add one —
  a query filter is not a reason for a schema change. A page with no revision 1
  matches no creator, which is the honest answer for a page whose author is not
  recorded. The value is an OIDC subject; a picker supplies it, and
  `currentUser()` is the spelling for "me".
- **`space` matches keys ordinally**, like `SearchService`'s own `s.Key ==
  spaceKey` and §6.3's exact-match doctrine. Wrong case yields the empty result a
  nonexistent key yields, which is the correct behaviour even though it is the
  less friendly one.
- **`label != "x"` means "does not carry label x", and therefore matches an
  unlabelled page.** This is a deliberate divergence from JQL, where the
  equivalent silently excludes issues with no labels at all and every user
  eventually learns to write `labels != x OR labels IS EMPTY`. RQL's `!=` is
  exactly `NOT (= )` and needs no such folklore; `IS EMPTY` exists for the cases
  where emptiness is the actual question. Same for `NOT IN`.
- **`ORDER BY` accepts `title`, `created`, `updated` only.** Sorting by label or
  space would need a join whose order is undefined for a page carrying several
  labels. The default when absent is `updated DESC`, applied at execution — the
  parsed AST keeps `ORDER BY` empty, so a query that merely passes through the
  builder does not grow a clause it never had.

**Functions.** `currentUser()`, valid only for `creator`; and `now()` with an
optional offset — `now("-7d")`, `now("+1h")`, units `w d h m`. Date literals are
ISO-8601. **`now()` resolves once per query execution**, so two `now()`s in one
query cannot disagree and a window built from two of them is never accidentally
empty.

**Date precision is part of the value.** A date written without a time denotes
the whole UTC day, so `created = "2026-01-31"` is a half-open range test and
`created > "2026-01-31"` means "after that day ends". An instant — including
every resolved `now()` — compares exactly. Compiling day-precision equality as
exact equality would match only a page created at precisely midnight while
looking like it worked.

### 22.3 Why classification is not queryable

`marking`, `classification`, `level`, `eyesOnly`, `caveat`, `prefix`,
`restricted`, `restriction`, `permission`, `clearance`, `group`, `nationality`
(and their plural and underscore spellings) are **refused by name**, with their
own error code and message.

The reason is §21.8's, applied to a query box. A filter over classification is a
**census of the classified estate**: `marking = SECRET` run against a
permission-filtered result set still tells its author how much SECRET material
exists in the spaces they can reach, how it clusters, and — run repeatedly with
different predicates — a great deal about content they cannot open. That is the
same argument that keeps a classification level out of every metric dimension,
and the query box is a more capable instrument than a dashboard. The same holds
for permission state: "which pages are restricted" is a map of where the fences
are.

Three properties of the refusal matter as much as the refusal:

1. **Not silently ignored.** An ignored predicate is worse than a refused one:
   the author believes a filter applied and reads the results as if it had.
2. **Not reported as an unknown field.** That would be a lie, and it invites a
   retry with a different spelling until something sticks. The message says *not
   queryable* and says why, so the author learns the rule once.
3. **A distinct error code** (`NOT_QUERYABLE_FIELD`), so a client can style it
   differently from a typo without matching on message text.

`text` gets its own third answer — `UNSUPPORTED_FIELD`, "not supported yet" —
because it is a real field deliberately absent from v1 rather than a forbidden
one. A text predicate has to compile onto the ranked hybrid FTS/vector path
(§9.3), which *ranks* rather than filters; shipping a `text ~` that filters in a
widget and ranks on the search page would be two behaviours under one name. The
message points at the `search` field.

### 22.4 An invisible space is indistinguishable from a nonexistent one

`space = "BLACKPROJECT"` where that space exists but the caller holds no role in
it returns **exactly** what a key naming no space at all returns: an empty
connection, no error, no hint (§6.7). The same holds for an unknown label and an
unknown creator.

This is structural rather than remembered. The compiler resolves space keys
against a map built from **only the spaces the caller holds a role in**, so an
invisible key and an imaginary key are both simply absent from it and both
compile to the same never-matches predicate. There is no branch that could tell
them apart, because nothing ever looked.

It follows that **validation errors are about syntax and vocabulary, never about
existence**. The validator has no database and no principal: it cannot report
that a space is unknown, and `parseRql` is safe to answer for any authenticated
caller precisely because of that. The failure mode to guard against is a future
"helpful" error — "no such space" — which would hand an outsider a space-key
oracle. It is pinned by test, on raw response bytes.

### 22.5 Execution: parse → validate → compile → post-filter → cap

1. **Parse and validate**, purely, in `RocketWiki.Core/Query`. Syntax errors stop
   at the first (past a shape error the token stream means nothing); vocabulary
   errors are all collected, with offsets, so an editor underlines them at once.
2. **Compile to a parameterized EF query.** No user text is ever concatenated
   into SQL. The one pattern language RQL reaches — LIKE, for `title ~` — has
   `%`, `_`, `[` and the escape character escaped with an explicit `ESCAPE`
   clause; `FullTextQueryBuilder` is the local precedent for treating a query
   mini-language as something to construct carefully rather than interpolate
   into. Plain provider-agnostic LINQ, so the SQLite tier exercises the same
   expression tree SQL Server runs.
3. **Narrow to spaces the caller holds a role in.** A space role is a *necessary*
   condition of `canView` (§6.4), so this changes no answer — and it is what makes
   the candidate cap meaningful rather than a lottery over the whole estate.
4. **Post-filter every candidate through `canView`**, per page, against its own
   ancestor restriction chain and its own protective marking, via
   `PermissionContextLoader.LoadBatchAsync` — the same shape search and
   `GetPagesByLabelAsync` use (§6.4.2/§6.7/§9.3). Clearance and eyes-only arrive
   free through `EffectivePermissionCalculator.Compute`; §21's gate is not
   reimplemented here. **The query decides which candidates are considered; it
   never decides which permission check runs.**
5. **Cap**, at both ends.

**Bounded cost.** Candidates are capped at 500 before permission filtering (an
over-fetch, so a restriction-heavy result set does not come back looking empty,
per §9.3's reasoning) and visible results at 100, where `totalCount` saturates —
`Query.Search`'s honest-cap idiom, stated rather than hidden: an exact
permission-filtered total means running `canView` over every candidate for a
number nobody scrolls to, and any cheaper count would be computed *before* the
filter and would leak restricted pages into it. The AST is capped too — 4096
characters, 512 tokens, 32 predicates, depth 8, 50 values per `IN` list, 3
`ORDER BY` terms — and a query over any of them is a positioned validation error,
so a pathological query fails before it reaches the parser's stack or the
database.

**No count includes a filtered-out row.** `totalCount` counts visible results
only. There is deliberately **no "capped" or "truncated" flag**: it would report
that at least 500 candidates matched, which is a count over rows the caller may
not know exist (§6.7). The cap is documented, not inferred from a response.

**Known cost, stated rather than discovered later.** No index supports RQL's
default ordering: `Pages` is indexed on `(SpaceId, ParentPageId, SortOrder)` and
on `AncestorPath` (data-model.md), so `ORDER BY UpdatedAtUtc DESC` over a
multi-space candidate set is a sort. The candidate cap bounds what comes *back*,
not what the engine sorts to produce it. That is acceptable at the scale this
ships into and is the obvious first thing to measure if a list widget is slow;
the fix is an index on `(SpaceId, UpdatedAtUtc)`, which is a migration and was
deliberately not written speculatively.

### 22.6 GraphQL surface

- **`pageQuery(query: String!, first: Int, after: String): PageQueryConnection!`**
  — the same hand-rolled connection shape as `search`
  (`totalCount`/`pageInfo`/`edges{cursor,node}`), plus an `errors` list and the
  §21.13 `aggregateMarking` over the whole permission-filtered result set. A row
  exposes **only** `page`, resolved through `PageByIdDataLoader`: nothing is
  projected onto it, for the reason `SearchHitType` documents — a projected title
  is an unauthorized copy that routes around object-level authorization (§6.7/§8).

  `errors` is the one addition to search's shape, and it is deliberate: an
  unparseable RQL string is *authored content* (it lives in a Markdown fence), not
  a server fault, so the widget rendering it must be able to show the author what
  is wrong and where — which a GraphQL top-level error cannot do positionally.

- **`parseRql(query: String!): RqlParseResult!`** — the AST as output, the
  canonical printed form, and positioned errors. It **executes nothing and touches
  no page data**: `Rql.Parse` is a pure function of the string, with no database,
  clock or principal, which is what makes it safe with no permission gate beyond
  authentication. The reasoning is confirmed rather than asserted, because "a
  parser needs no authorization" stops being true the moment validation consults
  the world: it resolves no space, label or user, so it cannot reveal whether any
  exists; its vocabulary and messages are compile-time constants identical for
  every caller (the same category as `protectiveMarkings` and
  `pagePropertyKeys`); and the only thing it returns about the input is the
  caller's own text, normalized.

**The canonical printer is a fixed point.** `Print(Parse(x))` reparses and
reprints identically, pinned by test over a corpus — because the builder UI's
round trip is build → print → store → reparse, and a printer that normalized
differently on the second pass would make a query drift a little every time it
was edited. Three rules get it there: keywords upper-cased, values *always*
quoted and escaped (so no value can be re-lexed as syntax), and parentheses
emitted only where precedence requires them, with the parser flattening
same-operator chains so `(a AND b) AND c` and `a AND b AND c` are the same tree.

### 22.7 Audit (§7) and telemetry (§15)

`pageQuery` audits as **`page.query`**, one row per executed query, Details
carrying the raw RQL text and the visible result count — the decision
`search.query` already makes and for the same reason: the audit table is the
grant-protected, append-only record of who asked what, and a query probing for
restricted terms is exactly the signal it exists to keep. Dedup keys on the query
text (like `gitlab.fetch`'s resource reference) so a page full of list widgets
gets a row per query rather than one for the request. An unparseable query is
audited too; `Outcome` stays `Success`, since `denied` is reserved for ABAC
refusals and RQL never produces one — restricted pages are *absent* from the
candidate set rather than refused.

`parseRql` carries `[NoAudit]`, and the justification is §22.6's rather than
convenience: §7 audits *user actions*, and an action has a subject and an access
decision. This has neither — no page, space or attachment to name, and no rule
evaluated for or against anybody — so a row could only record "someone typed a
string", which an editor with live syntax checking would write per keystroke.
Every query that actually runs is audited.

**An RQL string never reaches telemetry.** It is user-supplied text, so §15's
rule applies unchanged: no span name, tag, event, baggage entry, or metric tag.
`TelemetryHygieneTests` drives a sentinel through both fields and sweeps every
ActivitySource and RocketWiki meter in the process.

### 22.8 Deliberately not in v1

- **`text`.** See §22.3 — it belongs on the ranked retrieval path, not here.
- **Page properties (§20.5).** `property["Status"] = "Draft"` is the obvious next
  field and is not built. It needs its own vocabulary decision (the key registry is
  admin-defined, so the closed field set would have to admit an open-ended key
  space), and §20.5's own caveats apply unchanged when it is: permission-filtered
  per page, joined to `Pages` so rows for soft-deleted pages do not linger into a
  report.
- **A structured AST input type.** See the opening of this section.
- **Saved filters.** No storage, no sharing, no named queries. A query lives in
  the page that embeds it; adding a saved-filter entity would immediately raise
  who may see whose filter, which is an access-control question this feature does
  not need to answer to be useful.
- **Aggregations.** No `COUNT`, no `GROUP BY`, no facets. Every count RQL could
  produce would need the same per-page permission filtering the row list gets, and
  a count is exactly the shape §6.7 forbids implying a restricted page with.
- **Cross-instance queries.** RQL runs against the local instance's pages,
  replicas included as ordinary read-only content (§12); it does not reach the
  other side.
- **No UI.** The backend: grammar, validation, compilation, enforcement, audit and
  the two GraphQL fields. The widget, the builder and the editor integration are
  the frontend's own piece of work.
