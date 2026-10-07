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
| Real-time | SignalR (MessagePack) | notifications, presence, CRDT co-editing relay (§8) |
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
  slug, current title, sort position, an optional icon, and a denormalized
  `CurrentContent` column (Markdown of latest revision) for full-text indexing.
  The slug is the page's *address* — `/spaces/{spaceKey}/{slug}`, unique across
  the whole space rather than among siblings, since the tree is absent from the
  URL so that moving a page never breaks a link (§17).
  **Addresses are case-insensitive in both halves**: `/spaces/eng/my-page` and
  `/spaces/ENG/My-Page` name the same page. Not via a case-insensitive
  collation — that had the two database providers enforcing different rules —
  but via canonical stored forms (space keys upper, slugs lower, applied at the
  persistence seam) plus normalized lookups. Uniqueness follows the address
  rather than the bytes, so `My-Page` beside `my-page` is refused as taken.
  Full treatment in data-model.md, including why labels are deliberately
  excluded: a label is a name, not an address.
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
  attributes: { nationality: ["NZ"] } (the one registered claim — see 6.2 and §21.4)
}
```

Rules always evaluate against the **token**, never the local mirror — a change
in Keycloak takes effect on the user's next token refresh, not next login.
The local `User` row mirrors claims at each request for display/admin UI only.

**Groups and nationality are the whole of it, and that is a reversal.**
`PrincipalBuilder` is a static with a compile-time claim list — `groups` and
`nationality` — so a claim nobody listed there can never become an attribute a
rule or a gate matches on by accident. It briefly carried two more: a
`clearance` attribute, compared against a page's classification level, and one
claim per configured selector category, whose literal `yes` made the holder
eligible for that category. Both went on 2026-09-04, with the gates that read
them (§21.2), because this deployment's Keycloak carries neither attribute: a
gate that compares against a claim nobody emits protects nothing — it denies
everyone on a made-up floor, or, given a default, calls the default a control.
The JIT mirror (§11.3) records `nationality` (for the admin roster) and
`groups` (for the profile page, §6.2), raw and never interpreted; it is display
data and never an authorization input — the gate reads the token.

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
Nationality is sensitive personal data: its mirrored values are visible to
instance admins only. The mirror records registered attributes and the
`groups` claim (§11.3) and nothing else — `nationality` is the only attribute
the marking side reads (§21.4), and the selector categories (§21.15) are
deployment configuration that names no claim at all.

**The profile page, and the census it accepts (product decision,
2026-09-03; reshaped 2026-09-04).** Every user has a profile page, readable
by every signed-in user (`userProfile(id)`, §8), showing the **group
memberships** the person's token carried at their last sign-in — the same
`groups` claim every access grant, role grant and restriction is written
against (§6.3). It is read straight from the mirror, sorted ordinally, and
interpreted by nothing: there is no gate to re-run over a group name. The page
says "as of their last sign-in" in words and carries no timestamp. This is a
deliberate widening of what `userDirectory` refused, and it is recorded here
so nobody mistakes it for an oversight: the directory carries no rule-engine
attribute because a directory of them is a who-holds-what census, and a
profile page is exactly that census one person at a time — an insider with
one ordinary account can learn, profile by profile, who is in which group, and
therefore whose account a given grant admits. The product owner accepted that
exposure on 2026-09-03; the everyday use is a colleague checking which groups
a person is in before writing a grant or a restriction meant to include them.
Two bounds hold the widening where it is. The directory itself is unchanged
(`UserRef` only), so a census is still one profile request per person rather
than one list — a rate, not a barrier, and it is stated as such rather than
counted as a control; and **nationality, email and last-seen remain
admin-only** on the audited `users` roster — nationality because it is
sensitive personal data, email and last-seen because together they are a
surveillance surface (§15's reasoning). The mirror is still never read for an
authorization decision (§6.1).

*What the page showed for one day, and why it stopped.* As first shipped the
profile showed the person's **clearance** and their **selector eligibility**
per category, both re-derived through the gates from the mirrored claims so
that the page said what the gate would have decided. Those gates left the
engine on 2026-09-04 (§21.2: this deployment carries neither attribute in
Keycloak), so there was nothing left to derive — the page would have shown a
floor nobody holds and an eligibility nobody has. Group membership replaced
them because it is the fact that actually decides access here, and the census
argument above was re-weighed for it rather than assumed to carry over:
knowing who is in `export-cleared` is closer to knowing whom a grant admits
than a clearance ever was, and the owner accepted that on the same reasoning.

**The registry is rule-builder vocabulary, not marking vocabulary.** The
`nationality` row's allowed values once doubled as the eyes-only caveat's
country list; they no longer do (§21.4 records the reversal), and the
additional-selector categories (§21.15) are not registry rows at all. Both
marking vocabularies come from somewhere an admin cannot edit at runtime — the
caveat's is fixed by policy and hard-coded, the selector catalog is deployment
configuration (`ProtectiveMarking:SelectorCategories`), reviewable in a diff
exactly like a connection string. Deliberately: a vocabulary that gates access
and can be edited in a UI is a vocabulary that can be widened, or emptied, in a
UI, with no deploy and no diff to review. The registry keeps its original job,
which is telling the rule builder which `attr` keys and values to offer.

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

Two confined departures from ordinal matching exist, both inside the marking
gate (§21) and neither in an `attr` condition: the eyes-only caveat's country
tokens (§21.4), and a selector's category and value tokens (§21.15), are
trimmed and compared upper-cased. The reason is the same in both places and is
spelled out in §21.4 — the two sides come from different systems that were
never guaranteed to agree on case, and failing closed on a casing difference
is an outage, not security. (A third, the literal `yes` of the
selector-eligibility claim, went with that claim on 2026-09-04 — §21.15.)

### 6.4 Access grants, role grants and page restrictions

Three kinds of `AccessRule`, with different semantics:

- **Access grants** — when the expression matches, the principal **may see
  this space's data**. An access grant also carries zero or more **selector
  values** (§21.15); the values a principal holds in a space are the **union**
  over every access grant they match, so grants add here exactly as they do
  everywhere else. An "open" space is an access grant with
  `{ "everyone": true }` and no selector values. With no matching access grant
  a principal sees nothing in the space, whatever else they hold.
- **Role grants** — when the expression matches, grant a role:
  `space-admin` ⊃ `editor`. Multiple grants OR together: your role is the
  highest whose expression you satisfy. A role grant confers **no visibility
  whatsoever**.
- **Page restrictions** — restrict-only, per action (`view`, `edit`). A
  restriction never widens access; it adds a further condition on top of the
  space access. Restrictions **accumulate down the page tree**: to act on a page
  you must satisfy the restrictions of the page *and every ancestor*.

**"Viewer" is not a role.** Viewing is holding a matching access grant, and
nothing else. The earlier model had one grant kind whose roles nested
`space-admin ⊃ editor ⊃ viewer`, so a role *implied* visibility: "who may read"
and "who may act" were one dial. Export-control audiences need them to be two.
A space's administrator is routinely outside its content's audience; an editor
for one compartment must not see another's; and a reader granted `APPLE`
material holds no opinion about who may edit it. Separating the two kinds makes
each question answerable on its own, and makes the answer to the first one
impossible to widen by answering the second: **roles never supersede access.**
A `space-admin` who matches no access grant can manage the space's grants,
settings, trash and restrictions and can read none of its pages (§6.5.2, §6.7).

Effective permission:

```
canView(page)   = S: some access grant in the space matches
                  AND the page's marking row exists                        [§21.5]
                  AND G: every selector granted by a matched access grant  [§21.15]
                  AND N: caveat empty, or nationality ∩ caveat ≠ ∅         [§21.4]
                  AND R: every view-restriction on page + ancestors passes
canEdit(page)   = canView(page) AND role grant ≥ editor
                  AND every edit-restriction on page + ancestors passes
                  AND NOT a replica space                                  [§12]
canManage(space) = space-admin role grant OR instance admin — needs no access
comment         = requires canView
```

The conjuncts G and N, with the availability check ahead of them, are the
**protective marking** (§21): every page carries a UK Government
classification, zero or more additional selectors and an optional eyes-only
caveat. The selectors and the caveat gate view access; the classification and
the national prefix are presentational — they say what the content is and are
compared against nobody (§21.12). The marking is a third kind of thing on top
of grants and restrictions, not a variant of either, and its defining property
is that it can only ever **subtract** — no grant, no restriction, and no role
widens a marking. It is applied inside the same `EffectivePermissionCalculator`
gate walk that evaluates S and R, so every read path inherits it structurally
rather than by remembering. Until 2026-09-04 the ladder had two more rungs, C
(clearance ≥ level) and E (eligible for every selector's category); §21.2
records why they went. Full treatment in §21.

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
- **Labels.** Creating a label in a space requires an access grant *and*
  `editor` (a role never supersedes access, §6.4; the refusal names
  `no-space-access` first) — tagging is routine, not a taxonomy decision
  reserved to admins. Attaching or detaching requires `canEdit` on that
  specific page.
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
marking conjuncts of `canView`: an instance admin without the selector grant
or the nationality a page's marking demands sees that page exactly as
everyone else without it does — as a `(protected)` placeholder, or not at all
(§6.7). Unlike a rule, an admin cannot edit their way past it either: re-marking
a page requires `canEdit`, which already includes the marking gate against the
page's current marking, so a page you cannot see is a page you cannot re-mark
downward. Nationality lives in Keycloak, not in RocketWiki, and a selector
value is conferred only by an access grant in the space — which an admin can
write for themselves, but only as an audited rule change (§6.5.2). Both doors
open by leaving a trace, or not at all.

**Nor do they hold space access by virtue of the role.** `canManage` needs no
access grant and an access grant confers no management (§6.4), and the instance
`admin` role is a management role. An instance admin who matches no access
grant in a space can open its settings and edit its grants, and sees every page
in it as protected — the same experience as a `space-admin` without a grant
(§6.5.2). The one thing they can do about it is grant themselves access, which
is a rule change and therefore audited (§7). That is the whole point: the door
opens by leaving a trace, never silently.

### 6.5.1 Space lifecycle

- **Create** requires instance `admin`. Nothing else can authorize it: a
  space that does not exist yet has no grants to evaluate a role against.
  **Creation is atomic with its first grants** — the caller supplies the
  initial grants and all of them commit in one transaction with the space.
  **At least one `space-admin` role grant is required**, so a space never
  exists in a state where nobody can administer it. **Access grants are
  optional, and the default is none**: a new space is visible to nobody until
  someone who can manage it grants access. That is the twin of "nobody can
  administer", resolved the opposite way on purpose — an unadministrable space
  is a deadlock (§6.5.2), whereas an invisible one is one audited grant away
  from being useful, and silently opening a new space to `everyone` is exactly
  the footgun this model exists to prevent. The SPA pre-fills the admin grant
  with the creator's own user, never `everyone`. A space whose administrator
  holds no access grant is a normal state, not an error (§6.5.2).
- **Rename, archive, and restore** require instance `admin` **or** that
  space's own `space-admin`, consistent with `space-admin` meaning "manage
  this space" (§6.4).
- **Archiving does not touch pages.** An archived space is hidden from
  browse and becomes read-only; its content is untouched and restore
  reverses it. This is deliberately unlike page delete (§6.4.1): delete
  removes content and therefore needs `canEdit` on every affected page,
  whereas archive removes neither content nor anyone's access to it once
  restored, so it needs no per-page check and no cascade. The
  archived-spaces listing (`archivedSpaces`) follows the live listing's
  visibility rule — an access grant or any role grant lists a space (§6.7) —
  and instance admins see every archived space, because restoring is theirs
  to perform; the live listing has no instance-admin arm, since an admin
  holds no space access by virtue of the role (§6.5).

Whether editors and access-grant holders should still *read* an archived
space, or whether archive hides it from everyone but its admins, is an open
question.

### 6.5.2 Who may manage access rules — and the bootstrap

Rule management requires instance `admin` **or** that space's `space-admin`.
The instance-admin arm is not a convenience: without it the model deadlocks.

A space with zero role grants confers no role on anyone, so `space-admin` is
false for every principal — meaning **nobody can ever create the first grant**.
Creation-atomic-with-first-grants (§6.5.1) prevents that state arising, and
the instance-admin arm is the recovery path if a space ever reaches it
anyway (every grant deleted, an import half-completed, a restore from a
partial backup). Without both, a space can become permanently unadministrable
with no way back that doesn't involve hand-editing the database.

**Managing without reading is the normal state, not a degraded one.**
`canManage(space)` is a `space-admin` role grant or the instance `admin` role
and needs no access grant (§6.4). A space's administrator who matches no access
grant edits its grants, settings and restrictions (page titles withheld) while every page in
it renders as a `(protected)` placeholder to them (§6.7) — they can see that
pages exist and what they are marked, and can read none of them. Listings that
would show content therefore require access as well as role where they show a
title: the trash listing is `access ∧ editor+`, not `editor+` alone (and has
no instance-admin arm — an admin without a role could not restore anything it
listed), and the manage-gated restriction listing stays open to the manager but
withholds the title of every chain page they fail a view gate for (§6.6),
because a role never supersedes
access. The admin's route to the content is the same as everybody's: an access
grant that matches them, which they can write for themselves and which is
audited when they do.

This does not weaken §6.5's core rule. Instance admins still **cannot read
around page restrictions**. They can change rules — which §6.5 already
allowed — and every change is audited with full before-and-after state (§7),
so widening access is always visible. Read-around leaves no trace; rule
changes do. That distinction is the whole design.

### 6.6 Tooling (ships with the feature, not after)

Rule systems fail in the UI, not the engine:

- **Rule builder** — visual AND/OR group editor with pickers for known groups,
  registered attributes, and users. No raw JSON editing for normal admins.
- **Two grant editors, not one** (§6.4): an *access* editor — a rule plus a
  picker of selector values drawn from the configured catalog (§21.15), any
  number of values, empty meaning the grant carries none — and a *role*
  editor, a rule plus Editor or Space admin. The space-creation
  form asks "who administers this space" (required, pre-filled with the
  creator) and "who can see it" (optional). Keeping the two on separate panels
  is the UI half of "roles never supersede access": there is no control by
  which choosing a role also chooses an audience.
- **Permission inspector** — "why can / can't user X see this page": shows
  whether an access grant admits the subject (`hasSpaceAccess`), the space
  role (role grants only, and reported even when access is absent, so a
  manager can see why they may manage what they cannot read), **every gate**
  as a structured pass/fail list — S, marking availability, G, N, R for view;
  replica, role and the edit chain for edit — and each restriction with
  pass/fail per condition. A selector gate row carries the category and value
  in question; the caveat row carries no country set of its own on the
  inspector, because the page's `marking` sits beside it; the availability
  row carries nothing, because nothing about the subject is in it.
  Note this needs a **non-short-circuiting** evaluation path: the enforcement
  gate stops at the first failing gate (correct and cheap), but an
  inspector that stops there can only ever show one reason, which is the
  least useful answer when several rules are in play. The explain path is the
  *same* gate walk with short-circuiting switched off — one ladder, one flag —
  so there is no second precedence order to drift; it is never a relaxation of
  the gate.
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

  **An ancestor the caller fails any view gate for contributes its rule but
  not its title** — the inspector, and the manage-gated `Page.restrictions`
  listing, render an empty `pageTitle` for it. This is not a rough edge to
  tidy away later: restrictions accumulate down the tree but markings do not
  (§21.5), so passing `canView` on a page says nothing about its ancestors'
  classifications or selectors, and both surfaces name every page in the
  chain. Without the rule, a caller viewing an OFFICIAL child could read a
  SECRET parent's title straight off a screen whose whole job is to explain
  rules — a page the tree shows only as `(protected)` and every omitting read
  path treats as absent (§6.7). §6.4.1 already
  treats titles as the sensitive part of a refusal ("not which ones, since
  their titles may themselves be restricted"); this is the same rule applied
  to the one surface that names an ancestor. Only the title is withheld: the
  rule id, page id, action, expression and pass/fail all still travel,
  because those are what the inspector exists to show and the caller is
  already subject to that rule.
- **Restriction banner** — a page with active restrictions shows a lock badge
  listing the effective rules, so authors know a page is limited.
- Known groups are accumulated from observed logins, avoiding a Keycloak
  admin-API dependency in v1. *Shipped: `KnownGroupRecorder`, called from the
  JIT-provisioning middleware (§11.3), records each group name a validated
  token carries the first time this instance sees it. It keeps a process-wide
  set of names already known to exist, so the steady-state cost on the hottest
  path in the system is a hash lookup and no query at all; a losing race on the
  unique index is swallowed rather than failing a login, because this is
  suggestion vocabulary and the rule engine accepts any string ordinally
  anyway (§6.3). Nothing here is ever read back for an authorization
  decision — the Principal is still built from the token, every request
  (§6.1). The "plus manual add" half is **not built**: there is no mutation
  for it, and the picker's other sources (groups named in existing rules, the
  caller's own token) cover the gap in practice.*

### 6.7 Enforcement points

Every read path enforces `canView`: page fetch, tree (filtered during the
walk, ancestor restrictions carried down the recursion), search (keyword +
vector results post-filtered, §9), attachments (auth-checked before
streaming), comments, and revision history. Rules are cached in memory (they're small and few);
evaluation is pure in-process boolean logic, so per-page checks are cheap.

**Denial is disclosed on the page view, the page tree and in-page links — and
nowhere else.** A page the caller holds an access grant for but fails a
marking gate or a restriction on comes back as a **`(protected)`
placeholder**: a server-constant title, the page's full marking label, and
every failing gate with its reason (§21.8) — and never its id, real title,
slug, timestamps, author, labels, children, or any rule expression. Four
surfaces disclose, and the list is closed:

- `pageAccess(id)` / `pageAccessBySlug(spaceKey, slug)` return
  `{ page, denial }` with exactly one non-null, and `null` only for a page
  that does not exist. The plain `page(id)` / `pageBySlug` are **unchanged**
  — `null` for denied and for missing alike, byte-identical, still pinned by
  test — so a client that only wants content never receives a placeholder.
- The tree (`pageTree`, `pageSubtree`, `PageTreeNode.children`) renders a
  denied node as a `ProtectedTreeNode` **leaf in its sibling position**; the
  walk never descends beneath it, and `pageSubtree` of a placeholder is empty.
- `Page.linkTargets` resolves the `page://` links in a page's own content — a
  nested field, deliberately, so the ids come from content the caller can
  already read and never from a root field that would be an unaudited
  existence oracle for any id a caller obtained elsewhere.
- `Page.parentDenial` sits beside the unchanged, null-when-denied `parent`, so
  a breadcrumb can say `(protected)` for a parent a viewable child sits under
  (markings do not accumulate, §21.5, so that case is real).

**When the caller matches no access grant in the space, only "you have no
access to this space" is disclosed and the marking is withheld.** S (§6.4) is
the space's own decision about who may see anything in it; disclosing a
marking to someone outside that audience would tell them what sensitive
material exists behind a door they may not open, which is the census §21.8
refuses. So the placeholder for a page reached by id or slug says only that —
and by slug it does say that: `pageAccessBySlug` in a space the caller cannot
enter answers the no-space-access placeholder rather than `null`, so
`(spaceKey, slug)` confirms a page exists at that address. That is the
bounded space-key oracle accepted with the "space denial only" choice, and
everything else is withheld — no marking, no title, no id. The tree for such
a space is **empty**, byte-identical to a space that does not exist; and
space listings omit it unless a role grant lists it. A space you hold no
grant of any kind in remains invisible. A space you hold only a *role* grant
in is listed — its administrator must be able to reach its settings — with
`viewerHasAccess` false, an empty tree, and the no-space-access placeholder
for any page in it reached by id or slug.

**Everything else still returns "absent", never "forbidden".** Search,
Ask-the-wiki, RQL and the page-list widget, label listings, the home feeds,
notifications, `Page.children`, comments, attachments, revisions, watch state,
the SignalR joins, space listings, the permission inspector, **the document
graph** (`pageGraph(spaceKey)`, and `Page.inboundLinks` / `outboundLinks`
with their counts) and **every MCP tool** omit a denied page entirely: no
placeholder rows, no gaps in ordering that imply something was removed, no
count that implies one. On those surfaces the reasoning is unchanged — a
result list is a *compilation* of what matched, and a placeholder in it is a
census of what the caller may not read, per query. The graph is the sharpest
case: a whole-instance graph is a compilation of everything, so a page the
caller cannot view is not a node, an edge exists only when the caller can
view *both* of its endpoints (an edge with one hidden endpoint would be the
census with the name left off), and a link count is the length of the
filtered list beside it, never one more. `Page.linkTargets` and
`Page.outboundLinks` read the same links and answer differently on purpose —
the first discloses a placeholder per denied target to a reader inside the
page, the second is the omitting view the graph draws. Each omitting surface
is pinned by test, plus a sweep that the placeholder vocabulary never reaches
its response. The full table is in §21.8.

**Why the reversal, and why only there.** The earlier rule made a denied page
indistinguishable from a nonexistent one on *every* read path, and it was
right about the leak it prevented: existence, and often a title, disclosed to
someone with no right to know. What it got wrong was the audience. A reader
who holds access to a space and meets a gap in its tree, or a link that goes
nowhere, cannot tell whether the page was deleted, moved, or carries a
selector they are not granted — and "ask a space admin for a grant" is only
possible if you know there is something to ask for. Inside a space, existence
is already half disclosed by links, breadcrumbs and conversation; what the
placeholder adds is an honest name for the gap and the reason for it, so that
the fix (a selector grant, a corrected nationality, a restriction revisited)
can be requested instead of guessed at. Outside the space, nothing changes: no
marking, no
position, no tree. The line is drawn at the access grant because that is
where the system already decides who is "inside".

**Always on, not a toggle.** An earlier plan (`docs/RESTRICTED-PLACEHOLDERS-PLAN.md`,
now deleted — this section supersedes it) proposed an opt-in instance setting
showing placeholders for restriction denials only, never for marking denials
(the plan called them classification denials; the level gated then).
Both halves were rejected. A per-instance switch means an "absent" answer has
two meanings — "does not exist" or "you may not see it and the operator chose
not to say" — and every test, help page, MCP client and audit reviewer would
have to know which instance they were looking at; one behaviour is the only
thing an auditor can be told. And disclosing restriction denials while hiding
marking ones makes the *gap itself* a signal: no placeholder would
mean "compartmented", which is precisely the disclosure the distinction was
trying to avoid. Disclosing every gate, uniformly, to everyone inside the
space, with no gate silently privileged, is the honest version.

**Disclosed to the caller, and recorded for the audit log, with the same
reason.** §7 requires denials to be recorded with the failing gate, which the
read services know and the API layer cannot infer from a bare `null`. So read
services return an **internal** result distinguishing *not found* from *found*
from *denied (with the complete explanation)*, and the resolver decides per
surface what to disclose — the placeholder, or `null` — while auditing the
denial. A denial reached through `pageAccess` writes one `page.view` Denied
row, and no Success row for a placeholder; placeholders inside a tree or a
link list write no row of their own, because the browse or view that
produced them was audited as one operation; a denied parent is one
`page.view` Denied row whether `parent`, `parentDenial` or both were
selected, since the sink deduplicates a subject within a request. A service
that returns bare `null` makes denial auditing impossible — that is the
anti-pattern to avoid.

**The recorded reason is deterministic.** A denial's audit reason names the
*first failing gate* in the fixed order S, marking availability, G, N, R
(§21.2) — the
placeholder lists every failing gate, the audit row names one — and within R
the *first failing restriction*, so "first" has to mean something stable:
restrictions are always evaluated **root-most ancestor first, the page's own
rules last**, and by creation time within a single page. A denial therefore
reports the outermost boundary the caller failed, reports the same rule on
every request, and reports the same rule the permission inspector (§6.6)
shows — the inspector exists to explain audit rows, and the two disagreeing
would make both useless. Ordering never affects the *decision*: every gate is
a conjunct, so which one is named is the only thing that depends on it.
Enforcing this is a loading concern, not a calculator one — the calculator
states the input contract, and a single loader in the data layer
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
| Action | `page.view`, `page.edit`, `page.move`, `space.browse`, `attachment.download`, `search.query`, `page.query`, `permission.change`, `audit.view`, `sync.import`, `space.export.enabled`, … |
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

`graph.view` (§6.7's document graph) — one row per `pageGraph` read, Details
carrying the scope (`instance` or the space key asked for), the space's id
as subject when the key names a live space, and the node and edge counts of
the graph the caller received — bounded numbers, never a title or a marking.
Always `success`, on the RQL reasoning: a compilation never produces an ABAC
refusal, because a page the caller fails a gate for is absent from it rather
than refused, and that includes a space-scoped read of a space the caller
cannot enter (the same empty answer and the same `success` row that
`labels(spaceKey)` and `search(spaceKey:)` already write; the tree's `denied`
row for that case belongs to a directly requested subject, which a scope
filter is not). It dedups on the scope, so two scopes in one document write
two rows. The per-page `Page.inboundLinks` / `outboundLinks` fields and their
counts write no row of their own — a listing continuation of the
already-audited page read, as `linkTargets` is — except that the race-only
re-decision of the subject page itself is recorded as a `page.view` denial
when it fails, exactly as `revisions` records its own.

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

`space.export.enabled` / `space.export.disabled` (§12) — the low-side switch
that starts (or stops) a space's content crossing the boundary, subject =
space, details `{ exported }`. Two action names derived from one domain event,
for the same reason `page.marking.set`/`.downgrade` split below: enabling is
the operationally risky direction, and its own action name is what lets a
reviewer find every space somebody opened for export with a single query. The
GraphQL field declares `space.export.enabled` for the coverage guard and for
denial rows — a refusal has no direction, and the enabling name is the one a
reviewer is looking for. A no-op call (the flag already holds the requested
value) writes nothing: a row claiming "enabled" when it already was would be a
widening that never happened.

`page.marking.set` / `page.marking.downgrade` (§21) — a page's protective
marking changed, subject = page, details carrying the full before-and-after
marking: `{ level, eyesOnly, selectors, prefix, previousLevel,
previousEyesOnly, previousSelectors, previousPrefix }`, with `selectors` an
object keyed by category (`{"FRUIT":"APPLE"}`) — the same shape the sync
payload uses, so there is one parser mindset. Two action names for one
mutation on purpose: downgrading (anything that lets somebody read the page
who could not before, which now includes removing or swapping a selector) is
the operationally risky direction, and its own action name is what lets a
reviewer find every widening in the estate with one query. Like access rules,
the audit log is the *only* history a marking has — the row itself is mutable
— so the before-state is load-bearing, not decoration. This is also the only
place an eyes-only country set or a selector value is written out in full;
§15 keeps both out of every telemetry tag.

`permission.change` snapshots carry an access grant's `selectors` alongside
its expression, so a replay reconstructs which values a grant conferred, not
only whom it matched. One discontinuity is recorded rather than papered over:
the `SplitSpaceGrantsIntoAccessAndRole` migration (§6.4) converted every
`viewer` grant into an access grant in place and inserted a **mirror** access
grant beside every editor and space-admin grant, so nobody's visibility
changed — and those mirror rows have **no `permission.change` row**. A replay
as-of before the migration reconstructs the old model exactly; as-of after
it, the mirrored access grants are absent from the replay and are derivable
1:1 from the role grants that do have rows. Writing audit rows from SQL inside
a migration, into a partitioned, composite-keyed, append-only table, is the
class of operation this design avoids; the migration's own comment says the
same.

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
  `{"reason":"restriction:{pageId}:{ruleId}"}` — or one of the marking and
  space-access tokens in §21.8 — the same details shape mutation denials use,
  whether the response was a `null` or a `(protected)` placeholder (§6.7).
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

**The protective-marking overhaul (§6.4, §6.7, §21.15) adds a third batch,
breaking on purpose** — the SPA is in-repo, the MCP DTOs pick their fields
explicitly, and `SchemaExportTests` enforces the export. Denial disclosure:
`pageAccess(id)` / `pageAccessBySlug(spaceKey, slug)` returning
`PageAccessResult { page, denial }` beside the unchanged `page`/`pageBySlug`;
`pageTree`, `pageSubtree` and `PageTreeNode.children` typed
`[PageTreeEntry!]!`, a union of `PageTreeNode | ProtectedTreeNode { title,
sortOrder, denial }` (no id, slug, icon, labels or children — the union makes
a placeholder structurally unable to carry them); `Page.linkTargets:
[PageLinkTarget { id, page, denial }]` and `Page.parentDenial`; `AccessDenial
{ placeholderTitle, noSpaceAccess, marking, reasons: [GateResult] }` over
`AccessGate { SPACE_ACCESS, MARKING_UNAVAILABLE, SELECTOR_GRANT,
NATIONAL_CAVEAT, RESTRICTION, REPLICA, ROLE }`, the last two
reachable only through the inspector's new `viewGates`/`editGates`. Grants:
`AccessRuleKind { ACCESS_GRANT, ROLE_GRANT, PAGE_RESTRICTION }` (`SPACE_GRANT`
gone) and `SpaceRole { EDITOR, SPACE_ADMIN }` (`VIEWER` **removed, not
deprecated** — a deprecated enum value the mutation must reject is a lie in an
access model); `AccessRule.selectorValues`; `createSpace(input,
initialGrants)`; `Space.viewerHasAccess` / `viewerSelectorGrants`; the
sketch's `Space.grants: [SpaceGrant!]!` is `[AccessRule!]!` with the kind as
discriminator, manage-gated. Markings: `PageMarkingView.ukPrefix: Boolean!`
and `selectors` (`prefix` removed); `SetPageMarkingRequestInput { ukPrefix!,
eyesOnly: [NationalCaveatCountry!]!, selectors }` — the caveat enum is on the
*input* only, the output stays `[String!]!` so a legacy token can never break
serialisation; `Query.selectorCategories` (names, descriptions and values — nothing about
any claim, because a category names none). MCP is unchanged in shape and keeps
omitting on every tool; `get_page_tree` drops placeholders before mapping.

**The profile page (§6.2, 2026-09-03) adds `userProfile(id: UUID!):
UserProfile`** — `id`, `displayName`, `hasAvatar`, `isExternal` and
`groups: [String!]!`, the memberships recorded at the person's last sign-in,
in ordinal order. Readable by any signed-in user; anonymous and unknown id are
both null, like `page`. Unaudited on `userDirectory`'s reasoning, with the
widening stated plainly in the field's own `[NoAudit]` declaration. Read
straight from one row's mirror (§6.2); nothing is derived. `userDirectory` is
unchanged.

**Dropping the clearance and eligibility gates (§21.2, 2026-09-04) is a
fourth breaking batch, and it removes rather than adds.** `CurrentUser` loses
`clearance` and `selectorEligibility` and keeps `nationality`; `UserProfile`
loses `clearance`, `clearanceName`, `clearanceRecorded` and
`selectorEligibility` (the `SelectorEligibilityStatus` type goes with them)
and gains `groups`; `SelectorCategory` loses `requiresAttribute`; `GateResult`
loses `requiredLevel` and `requiredLevelName`, since no gate has a level to
require; `AccessGate` loses `CLASSIFICATION` and `SELECTOR_ELIGIBILITY` and
gains `MARKING_UNAVAILABLE` (§21.5). The server refuses a document that still
selects a removed field, which is the honest failure — a client quietly
handed a null clearance would draw a floor that no longer exists.

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

#### Presence: who's here, carets

Live presence on a page — avatars of everyone viewing, and their text
carets — over the same hub. It divides into two tiers that cost very
different amounts:

| Tier | Needs | When |
|---|---|---|
| **Viewer presence** — avatars, "3 people viewing", who is editing | nothing beyond the hub | v1 |
| **Text carets and selections** — caret inside the document, remote selection highlights | **a shared document state, i.e. CRDT** | with co-editing |

The first is just ephemeral state broadcast between clients. A **text
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
- **Binary on the wire.** The hub uses the MessagePack protocol, which
  keeps frames small and is what the Yjs binary updates need anyway.
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
Replicas refuse co-editing at the join gate (§12); presence still works on
a replica — there's just nothing to merge. Live updates are
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
- Embedding runs as a **background job driven by a state scan**, not by the
  domain-event pipeline: a page is due when no `PageEmbeddingState` row records
  its current revision as embedded. That was a deliberate change from the
  event-subscription design this line used to describe, and the reasons are on
  `PageEmbeddingState` itself — a scan also catches the sync CLI's
  out-of-process imports (§9.4), which no in-process event hook would ever see,
  and it is restart-safe, since a crash between save and embed loses nothing.
  It never blocks saves. Chunks are content-hashed so only changed chunks
  re-embed. Failures retry; while the endpoint is down, search degrades
  gracefully to FTS-only.
- **A page that can never be embedded must not starve the queue.** The scan
  is oldest-first and a page stays due until it succeeds, so one page the
  endpoint rejects for its own reasons — an oversized chunk, a provider
  content filter — sits at the head of every batch forever. Three rules keep
  that local to the page. A failure moves the run on to the *next* page
  rather than abandoning the batch; only a run of consecutive failures (the
  endpoint really being down) stops a run early. After `MaxAttempts`
  consecutive failures the page's **current revision** is quarantined and
  skipped by the scan, so the queue drains past it. And quarantine is keyed
  on the revision, not the page: editing the page is the only cure available
  to an author, so a new revision gets a fresh attempt budget and lifts the
  quarantine by itself. A quarantined page is silent by construction —
  search simply returns less — so it is reported through
  `rocketwiki.embeddings.pages_quarantined` and a per-page warning log
  carrying the page id and revision (§15: the id goes in the log, never in a
  metric dimension).
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

**Known limitation, and it is structural: without embeddings configured, a
sentence-shaped question mostly returns `NO_RESULTS`.** Retrieval passes the
question verbatim to `ISearchService`, and both keyword legs are built for
keywords — the SQL Server leg ANDs every term, so every word of the question
would have to appear on one page, and the SQLite leg wraps the whole question in a
single `LIKE '%…%'`. When an embedding endpoint is configured the vector leg
handles natural language and hybrid fusion covers this; when it is not, §9.2's
"unconfigured means keyword-only, structurally" applies to the assistant too, and
the honest description of `askWiki` in that mode is *keyword lookup with a
generated summary*, not question answering. **Local development runs in exactly
this mode**, so this is what a developer sees first. Fixing it properly means
either a retrieval-oriented mode on `ISearchService` (extract terms, rank by any
rather than all) or requiring embeddings for `askWiki` — a design decision
deliberately deferred rather than patched at the call site, because changing what
`search()` means for everyone to suit one caller is the wrong trade.

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
boundary (§9.4), reached via the Aspire `assistant` connection string, its
own `Ai:Assistant:Endpoint`/`ApiKey`/`Model` keys, or `Ai:ChatModel` against
the shared `Ai:BaseUrl` — per value, in that order — and fail-closed (§15): no
default exists, unset means the feature is absent and `askWiki` answers a
typed `NOT_CONFIGURED` payload fact (never a GraphQL error — the §18
degradation pattern, including `UNREACHABLE` on endpoint failure: one
attempt, bounded timeout, no retries). v1 is non-streaming and stateless:
no conversation memory, each ask retrieves fresh under the current token.

**The question is bounded, and the bound is readable.** `Ai:MaxQuestionChars`
(default 2000) refuses an over-long question *before* retrieval and before
anything is sent — refused rather than truncated, so what gets cut is the
asker's choice rather than a silent prefix. The `assistantStatus` query
reports that number (and null when the assistant is unconfigured: no feature,
no bound), so a client can warn while a question is being typed instead of
only after it is refused. It is instance configuration, not content — the
`gitlabStatus` shape — but unlike `gitlabStatus` it answers an anonymous
caller with the same shape as an unconfigured instance: `askWiki` refuses
anonymous callers outright, so there is no feature there to describe, and an
unauthenticated inventory of what a classified deployment runs is the same
thing production introspection was closed to avoid.

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
claims (JIT provisioning). The attribute mirror records exactly two claims:
`nationality` (the registered attribute, §6.2, for the admin roster) and
`groups` (for the profile page, §6.2) — the same two `PrincipalBuilder` maps,
so the mirror and the principal cannot disagree about which claims exist.
Raw values, in token order, every key present (an empty list when the token
carried no such claim), so "recorded as none" is distinguishable from "never
recorded". It briefly also recorded the clearance claim and every configured
selector claim, so the profile could re-run those gates over them; the gates
went on 2026-09-04 (§21.2) and the claims stopped being recorded with them.
The gate still reads the token (§11.4).

### 11.4 The request principal

The request principal (groups + attributes) is built from the token and
handed to the access-rule evaluator (§6) — the local mirror is never used
for authorization decisions.

### 11.5 Keycloak realm requirements

Keycloak setup required: three protocol mappers on the RocketWiki client —
`groups`, `nationality` and the realm `roles` — so the claims actually appear
in access tokens, plus an audience mapper naming the API client. That is the
whole list: `KeycloakClaimParityTests` pins the dev realm to exactly it, and
`src/RocketWiki.AppHost/keycloak/README.md` says what production must
reproduce.

Two of those claims have a fixed contract:

- `nationality` must emit tokens from the fixed caveat set `AUS`, `CAN`, `NZ`,
  `UK`, `US` (§21.4). Any other token is ignored by canonicalisation, so a
  mapper still emitting `GB` leaves the user with **no** nationality — every
  eyes-only page denied, visible as an empty `me.nationality` — rather than a
  silent partial match. It is the only attribute the marking side reads.
- `roles` must be a flat claim (Keycloak's nested `realm_access.roles` is
  never flattened by the bearer handler); it is what the instance-admin check
  reads (§6.5).

**Two mappers this section used to require are gone, and production must not
add them back.** A `clearance` mapper emitting a level's wire name, and one
single-valued mapper per configured selector category whose `yes` marked the
holder eligible, fed the C and E gates (§21.2). Those gates were removed on
2026-09-04 because this deployment carries neither attribute in Keycloak; a
claim by either name is now read by nothing, so mapping one would be inert at
best and, at worst, a promise to an operator that a control exists.

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

  **"Full snapshot" means the whole *What travels* table below, not just
  pages.** A baseline emits pages (with their revision history and marking),
  and then — as their own lines, in the same payload shapes the incremental
  writer produces — every page restriction, label, page
  property, comment and live attachment in the space, with attachment bytes
  packed into `blobs/` exactly as an incremental drain packs them. It did not,
  for a long time, and the gap was not symmetric: a missing comment is a
  completeness bug, whereas a page that was restricted on low landing on high
  with *no restriction row* is readable by every viewer of the replica. That is
  fail-open, which nothing else in this section is. Restrictions are therefore
  emitted first, and the ordering is load-bearing in general: pages lead,
  because everything after them references a `pageId` the import side resolves
  against lines it has already seen.

  Two asymmetries, both deliberate. **Comment tombstones cross** — a deleted
  comment is a row, not an absence (§5/§6.4.2), and a live reply's
  `ParentCommentId` would have nothing to point at without it, so parents are
  emitted before children regardless of timestamp. **Soft-deleted attachments
  do not** — nothing references them by FK, and shipping the bytes of content
  somebody deleted across a boundary that cannot take them back is the wrong
  default. A baseline is the live state.

  **Extending a baseline this way needed no format bump, and the reason is
  worth keeping** for whoever extends a bundle next. Every added line is an
  event *type* the importer has always understood, in the byte-identical
  payload shape the incremental writer already emitted, and baseline lines
  carry `SequenceNumber` 0 — which the import side applies without advancing or
  gap-checking the per-space sequence. So `formatVersion` stays 2 and the entry
  stays `events.v2.ndjson`: an **older** importer reading a newer bundle
  applies *more* of the space correctly and never refuses, and a newer importer
  reads an older bundle exactly as before. A version bump is for changing what a
  line *means* or where lines *live* (as format 2 did, renaming the entry so a
  format-1 importer refused loudly rather than silently discarding history) —
  not for emitting more of what a line already meant.
- Import applies bundles **strictly in order and refuses gaps**: if bundle 41
  hasn't been applied, 42 waits. Apply is idempotent, so duplicate delivery
  is harmless.
- The manifest hash chain makes a missing, reordered, or tampered bundle a
  detected error, never a silent absorb. Every import is audited
  (`sync.import` with bundle id and event range).
- **Attachment bytes are re-hashed on import, and the chain reaches them
  without covering them.** `PayloadSha256` covers the events file only; the
  binaries live in their own `blobs/` entries outside it. What binds them is
  that each entry is *named* for the SHA-256 of its own content and the event
  line referencing it carries the same hex string — and that line is inside the
  hashed payload. So the importer recomputes the hash of every `blobs/` entry
  and requires it to equal the entry's own name (which must itself be a
  well-formed SHA-256). Substituting a file would need a preimage; renaming the
  entry breaks the reference from a hash-covered line. Refused as a
  `sync.import.refused` of the same family as a chain break, and — like the
  per-space sequence check — as a **pre-pass**, because attachments are written
  to storage as their events apply and a half-applied bundle leaves blobs
  behind that nothing collects.

  What this does **not** cover is omission: stripping a `blobs/` entry makes
  that attachment land with no content rather than with the wrong content, and
  that is deliberate — export legitimately skips an attachment whose object is
  missing from its own storage (§10), so refusing on absence would turn an
  origin-side fault into a boundary the operator cannot cross. The omission
  surfaces at the first download.
- **The bundle's declared origin must match the one the importer was told.**
  `manifest.instanceId` is compared against the operator's
  `--origin-instance-id`. Every replica space, the import position and every
  per-space sequence on the high side are keyed by the latter; importing
  instance A's bundle as if it came from B splices two streams into one
  position, and the strict ordering that position exists to enforce becomes an
  ordering over nothing.
- **A bundle is decompressed under ceilings** (entry count, per-entry and total
  uncompressed size, event-line count). Every integrity check above happens
  *after* something has been decompressed, so none of them stops a zip bomb —
  and the declared size of a zip entry is a number its author chose, so the
  reads count the bytes that actually arrive rather than trusting it. Refused
  as `sync.import.refused` like any other integrity failure. The Confluence
  importer applies the same discipline to a space export, which is likewise an
  archive from outside this system (§13).

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
history. **Format 3** adds the `selectors` object to every marking payload
(§21.10) and stores events under `events.v3.ndjson`, for exactly format 2's
reason: a format-2 importer would absorb a selector-bearing page while
silently discarding its selectors — a *widening* on the high side, the one
unsafe direction — whereas its missing-entry guard refuses loudly. A format-3
importer accepts formats 1 and 2 as before.

**Flagging a space exported is `setSpaceExported`** (GraphQL mutation,
**instance admin only**), audited as `space.export.enabled` /
`space.export.disabled` (§7). Deliberately *not* the "instance admin **or**
space-admin" gate rename/archive/restore share (§6.5.1): those curate a space
inside this instance, whereas this one decides that a space's content starts
crossing into another security domain — a judgement about the boundary, not
about the space. It is idempotent and silent when the flag already holds the
requested value (an audit row claiming export was "enabled" when it already was
would put a false widening in front of the reviewer the row exists for), and it
refuses a replica with `ReadOnlyReplicaError` for the reason immediately below.
Setting it is what makes the outbox begin journaling; `--baseline` refuses
until it is set.

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
| Page **protective markings** (§21) — a page that is SECRET on low is SECRET wherever it lands, **selector values included** (they cross verbatim with the marking; bundle format 3, §21.10). Same fail-closed reading as restrictions: an eyes-only country outside the fixed set, or a selector whose category or value the high side has not configured, matches nobody, so the page arrives *more* restricted — visible to nobody until a high-side operator configures the category and a high-side admin grants the value. A page can never land unmarked — an upsert carrying no marking creates the row in the recorded *unavailable* state, readable by nobody until the origin sends a declared marking (§21.10); it is that recorded state that denies, not the level, which is presentational (§21.12) | Nationality — each side's Keycloak decides who holds which (§21.4), exactly as it decides group membership; and selector **grants** — the high side's access grants decide which values its readers hold in the replica, exactly as they decide who may see it at all (§21.15). There is no clearance and no selector eligibility on either side to stay local: the level is presentational everywhere (§21.12) |
| | Space **lifecycle and identity** — name, description, archived state, and the default page (`HomepageId`). Spaces aren't a sync event type: import creates the replica row from the space key alone, so renaming, archiving or re-pointing a replica's default page is legitimate local curation (like grants), not a blocked content write. The default page has a second reason of its own: it is a page *reference*, and each side holds a different subset of pages — a low-side homepage could name a page the high side has no row for. Page ids survive sync, so a replica's admin picks from what actually landed there |
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

The CLI is `RocketWiki.Sync export --output <dir> --instance-id <id>
--attachments-root <dir> [--baseline <space-key>]` and `RocketWiki.Sync
import --bundle <file-or-dir> --origin-instance-id <id> --attachments-root
<dir>`. The database connection string comes from
`$ROCKETWIKI_CONNECTIONSTRING` or `--connection-string-file <path>` rather
than from argv — a credential on the command line is readable by every other
local user (`ps`, `/proc/<pid>/cmdline`) and lands in shell history and crash
dumps; `--connection-string <value>` still works and is documented as the
discouraged option. `--baseline` runs
exactly once per newly exported space and is refused for a space that isn't
flagged exported or that this instance doesn't own — a check the **export
service** makes, not only the CLI, so a second caller cannot bypass it the way
the incremental path's `SyncOutboxWriter` gate never could be. Import in directory mode
applies every `bundle-*.zip` in bundle-number order; exit code 2 (vs. 1 for
usage errors) marks integrity refusals so a scheduled job can page on them.
An integrity refusal also leaves a durable audit row: `sync.import.refused`
on the sync channel, recording the bundle file name, origin instance, and
refusal reason (gap, chain break, payload-hash mismatch, attachment-blob hash
mismatch, origin mismatch, size-limit exceeded, per-space sequence gap,
unsupported format, or unreadable file). The last of those covers a bundle
whose bytes are simply malformed — truncated JSON, a missing required payload
key, an unparseable date, an event type this build does not know — and it is
listed here because it is easy to build the refusal machinery and then not
reach it: a throw that escapes the CLI's catch is an exit code and a stack
trace where §7 requires a durable row. It is written on a fresh unit of work — never the
one holding the partially-applied bundle — and is deliberately not
`sync.import` with a `denied` outcome: §7's denied names a principal
refused by a failing restriction, and an integrity refusal has neither; the
refusal itself is the successfully-completed action being recorded.
One event type is known and deliberately *skipped* rather than applied or
refused: `PageEntry` (wire value 11), which belonged to the removed page-entries
feature. An origin that has not upgraded may still drain rows of that type into
its bundles; the importer steps over such a line, advancing the per-space
sequence as for any applied one, so the changes beside it land. The number is
reserved and never reissued — a low-side outbox may still hold rows carrying
it, and a reused value would be read there as whatever new meaning it was
given.
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
they cannot drift from *current* behaviour — but that alone never fails when
a regenerated file differs from the committed one. That last mile now exists:
the CI job runs the suites and fails on a dirty working tree under
`tests/fixtures/converted-markdown`, printing the diff. Drift is therefore
caught in CI, not by an assertion in the generating test — which is why that
test deliberately makes no claim about the file it just wrote.

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

**A page that will not convert at all is one page's problem, not the run's.**
There is no transaction spanning an import — each service commits as it goes
— so an exception escaping the conversion loop at page 700 of 900 left 699
pages and every attachment already written, no report (it is produced after
the loop), and a re-run blocked by the space key existing. The trigger is
ordinary: any named HTML entity outside the converter's table, which is most
accented characters.

Such a page is therefore **imported anyway, with its original Confluence body
preserved verbatim inside a fenced code block**, and reported as a conversion
failure. Of the three available outcomes this is the only one that loses
nothing. Aborting is the worst. Importing an empty or placeholder page is the
second worst: the page exists, looks migrated, and the content is simply gone,
which nobody discovers until someone needs it. Skipping the page is worse
still, because the importer skips a whole subtree when an ancestor is missing,
so one bad entity near the root drops everything under it. Preserving the text
keeps the page readable, searchable and convertible by hand, and keeps the tree
intact. The fence is grown past any backtick run in the body, so a Confluence
code macro cannot close the block early and spill raw XHTML into the page.

Comments degrade the same way, a dry run predicts exactly the same outcome, and
the one remaining backstop — an unexpected infrastructure failure — returns the
partial report rather than throwing, so what was written is always on record.
There is still no resume: recovery from a part-way stop is to read the report,
delete the partial space, and re-run.

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
  permissions it found so an admin can re-apply them deliberately. The
  report carries every space permission and page restriction the export
  contained, **verbatim**: Confluence's own type and subject strings,
  including types this importer has never seen, because a permission
  filtered out for being unrecognised is the one most worth a human's
  attention. Finding any sets the report's "needs review" flag even when
  every page converted perfectly — an import is not done while the
  destination is more open than the source was, and a clean-looking report
  is how that gets missed. An export carrying none says so explicitly, so
  that "there were none" and "nobody looked" never read the same.
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

// The API and the SPA run as the images the deployment ships — the same
// Dockerfiles, built by the AppHost on every start — not as a project and a
// Vite dev server. The AppHost "serves both local development and the
// deployment artifacts" by running the artifacts.
var api = builder.AddDockerfile("api", "../..", "Dockerfile.api")
                 .WithHttpEndpoint(port: 5079, targetPort: 8080)
                 .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                 .WithReference(sql).WithReference(minio)
                 .WithReference(kc).WithReference(ai);

builder.AddDockerfile("web", "../..", "Dockerfile.web")
       .WithBuildArg("VITE_OIDC_AUTHORITY", "http://localhost:8080/realms/rocketwiki")
       .WithHttpEndpoint(port: 5173, targetPort: 8080)
       .WithEnvironment("API_UPSTREAM", api.GetEndpoint("http").Property(EndpointProperty.HostAndPort))
       .WaitFor(api);
```

- **Dev:** `aspire run` starts everything — SQL Server, MinIO, a Keycloak
  seeded with a dev realm, then the API image and the web image (nginx serving
  the Vite build, proxying to the API) — with the Aspire dashboard for logs,
  traces, and health. New contributors get a working stack from a clone plus
  one command, which matters when the system has this many moving parts; and
  what they run is what ships, so the Dockerfiles and the nginx config are
  exercised on every start instead of only at deployment. The browser-facing
  host ports are pinned (the SPA's Keycloak and draw.io addresses are Vite
  build-time constants, and the realm's redirect URIs name the SPA's origin),
  and Keycloak's issuer is fixed to the browser's address (`KC_HOSTNAME`) while
  its backchannel URLs follow the container network, so one token validates
  for an API that reaches Keycloak by a different name than the browser does.
  The fast inner loops — `dotnet run` on the API, `npm run dev` on the SPA —
  remain, and point at this stack.
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
- **And SignalR is not the only blocker — naming only it is a trap.** The
  embedding background job (§9.2) has no claim protocol: no lease column, no
  `UPDATE…OUTPUT`, no application lock. Its scan is a plain read and its write
  a plain upsert, so two replicas select the same oldest-N due pages and race
  the unique index on `PageEmbedding`. The loser's unique-violation is caught
  by the job's generic failure handler and charged to the PAGE, which now also
  counts towards that page's quarantine ceiling — so scaling out would slowly
  remove pages from semantic search and report it as content failures. Adding
  Redis alone satisfies the SignalR precondition and breaks this one silently.
  Scale-out needs both, in the same change.
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
`pullPolicy: Never`). The health-probe gap this paragraph used to record is
closed: `MapDefaultEndpoints` now has its config gate
(`IsDevelopment() || HealthEndpoints:Enabled`), the chart defaults
`probes.mode: http`, and the api Deployment sets `HealthEndpoints__Enabled=true`.
`helm lint` / `helm template` pass; nothing has been applied to a cluster (see
§16's standing caveat).

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
  specific reason, which is where it belongs. The marking gates collapse the
  same way (§21.8): `marking:unavailable` to `marking-unavailable`,
  `caveat:eyes_only` to `caveat`, every `selector:*` token to a single
  `selector` — never per category and never per value, because a
  denials-by-codeword series is a map of the compartmented estate —
  and `no-space-access` as itself. An unmapped token lands in `other` rather
  than minting a series. That is also what keeps the retired
  `classification:{level}` mapping from coming back by accident: the token is
  never minted now (§21.12), and were one ever to appear it would land in
  `other` rather than reopen a per-level series.
- **A marking never reaches a tag.** Not the label, not a level, not a caveat
  country, not a selector category or value (§21.8, §21.13). The selector
  catalog's startup log line prints the *count* of configured categories and
  nothing else. The hygiene tests plant a sentinel selector value and a
  sentinel country on a page, drive the disclosing surfaces (§6.7 — the
  placeholder, the tree, `linkTargets`, MCP) so the label demonstrably leaves
  the process in the response body, and assert it reaches no span, tag,
  event, baggage entry or metric.
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
| 0 | Walking skeleton | **done** | Aspire AppHost + ServiceDefaults, Vite app scaffolded, Keycloak dev realm with the §11 protocol mappers, schema-drift + audit-coverage guards. `aspire run` verified end to end on 2026-08-28 (see the caveat section below); since 2026-10-07 the AppHost runs the API and the SPA as the images `Dockerfile.api`/`Dockerfile.web` build, so the Vite app is in the AppHost as the web image rather than as a dev server |
| 1 | Editor spike ⚠️ | **done** | TipTap + Markdown round-trip for the full v1 feature set, proven against a real editor instance. Was the highest-risk item; it held. |
| 2 | Core wiki | **done** | Rule engine, EF model proven on SQLite, domain-event pipeline (audit in the same transaction), page CRUD + subtree delete, permission-filtered reads (incl. §6.7's not-found-vs-denied result with denied-read auditing), GraphQL resolvers + object-level authorization (adversarially tested), access-rule management with replay-provable history, space CRUD |
| 3 | Content features | **done** | Attachments (S3 + filesystem providers; S3 now verified against live MinIO — upload, download and object placement), comments, labels — all wired end to end and audited |
| 4 | Search & polish | **done** | `search`/`labels` API matching the shipped UI operations, permission-filtered with section attribution; the SQL Server FTS path (CONTAINSTABLE, inflectional stemming) is CI-verified against a real FTS-enabled engine by the §14 Testcontainers tier on every run — the `sqlserver` job fails if the tier skips — while the SQLite LIKE fallback is what the container-free tiers exercise; trash/restore, space management UI, rule builder + permission inspector, audit log viewer, import report UI |
| 4b | Notifications & presence | **done** (live hub unexercised) | SignalR hub, watches, delta-based mentions and reply notifications, per-recipient `canView` fan-out (re-checked at read time too), persisted notification list incl. `sync_bundle_landed` rows from the offline import. The SPA now generates its client from the exported `schema.graphql` (placeholder deleted), runs the real SignalR transports by default (fakes only behind `VITE_FAKE_REALTIME`, for tests and backend-less dev), and wires the bell (persisted list + live push, de-duplicated by row id), watch/unwatch on pages and spaces, and the §12 admin sync status page. Per the standing caveat no browser has ever actually connected to the hub |
| 5 | Migration | not started | Importer against a real Confluence space export; trial runs and fidelity review |
| 6 | Low/high sync | **done** (baselines are current-state-only) | Outbox journal, `RocketWiki.Sync` export/import CLI with hash chain, baseline snapshots (documented simplification: no revision history), replica read-only enforcement with `originInstanceId` in the error, admin `syncStatus` query |
| 7 | Semantic search | **done** (fake endpoint; exact-scan vectors) | Heading-boundary chunker over the shared anchor primitives; `PageEmbeddingState`-driven polling background job (covers sync-CLI writes; per-chunk hash re-embed; failure backoff; trash purge); `IEmbeddingGenerator` via Microsoft.Extensions.AI.OpenAI from the Aspire `embeddings` connection string — unconfigured means keyword-only, structurally; hybrid RRF inside the same `search` field (no schema change), canView after fusion, semantic hits deep-link via chunk attribution recomputed post-canView; `rocketwiki.embeddings.*` telemetry with a sentinel hygiene test. Native `vector(1536)` shipped (the AlterPageEmbeddingToNativeVector migration) with in-engine `VECTOR_DISTANCE` scoring, CI-verified by the §14 Testcontainers tier; only the DiskANN index remains deferred, with engine-verified, tripwire-tested blockers (§9.3, data-model.md); SQLite maps the column to a blob and keeps the in-memory cosine fallback |
| 8 | MCP server | **done** (no live Keycloak/OAuth dance yet) | `/mcp` (streamable HTTP, stateless, in-process) via the official C# SDK; RFC 9728 resource-metadata discovery pointing at Keycloak; four read-only tools over the shared service layer; per-call `mcp`-channel audit incl. client name and denied-read reasons; audit-declaration guard extended to tools; `rocketwiki.mcp.*` telemetry |
| 9 | k3s deployment | **authored, unexercised** | Dockerfiles + Helm chart in-repo (`deploy/`), migration Job via EF bundle, Traefik ingress with WebSocket upgrade, secrets by reference, probes (TCP until health endpoints get a non-Dev config gate), offline image path. `helm lint`/`template` pass; nothing applied to a cluster; restore drill unrun |
| 10 | Co-editing | **done** (live hub unexercised) | Relay-only Yjs edit sessions over the existing hub: canEdit-gated join with denied-join auditing, seeder designation + reseed protocol, log cap + empty-session GC, rule-change eviction extended to edit groups, PageRevisionContributor attribution wired through updatePageContent (forgery-proof: server-side session data only), session-scoped audit on the new realtime channel, `rocketwiki.coedit.*` telemetry with sentinel hygiene test. SPA phase 2: SignalR Yjs provider over the shared hub connection (join/seed/replay, batched updates, awareness carets, log-cap auto-save-and-reseed, eviction, documented reconnect), collaborative TipTap mode with solo fallback as the default degradation, session-base saves with contributor attribution surfaced on save |

### The standing caveat, and what happened when it was lifted

This section used to say that everything above was verified by **tests**, not
by running — that no container had ever started in development, no realm been
imported, no token decoded, no S3 call made against a live bucket. On
2026-08-28 a working Docker runtime finally arrived and all of that was done:
`aspire run` from empty volumes, migrations applied to a real SQL Server 2025,
the dev realm imported, every dev user logged in through PKCE, attachments
round-tripped through MinIO, and OTLP traces/metrics/logs captured off the
wire. The README's status section records exactly what was observed.

**The result is the argument for this caveat having existed.** Twelve defects
surfaced once the system was actually run — five of them in shipped
application code that both the test suite and repeated human review had passed
over. Three came from the container run itself:

1. The dev realm's tokens carried **no `sub` claim** — a realm-level
   `clientScopes` array suppresses Keycloak's built-in scopes, and `sub` has
   lived in the `basic` scope since Keycloak 24. `PrincipalBuilder` returns
   `null` without it, so every request from a valid login was anonymous.
2. **No caller could ever be an instance admin.** ASP.NET's default inbound
   claim map renames `roles` to `ClaimTypes.Role`; `IInstanceRoleAccessor`
   read only `"roles"`. Invisible to the test tier precisely because its fake
   auth handler does no claim mapping — the test double was *more* faithful to
   the code's assumption than reality was.
3. **Every S3 upload failed over plain HTTP.** `DisablePayloadSigning` was
   hard-coded true, which the AWS SDK refuses without TLS — i.e. against
   exactly the in-network object stores §9.4 describes.

Three more surfaced the moment a browser was pointed at the running stack —
each one blocking the step before it could even be reached:

4. **Sign-in never completed.** The OIDC user store and state store shared one
   in-memory backing. Tokens belong in memory (§11), but *sign-in state* is
   written before the browser leaves for Keycloak and read after it returns, so
   the navigation wiped it and the code exchange failed every time.
5. **The notifications hub never connected.** StrictMode's mount/unmount/mount
   raced `start()` against `stop()`, and `withAutomaticReconnect` only resumes a
   connection that succeeded once, so realtime stayed dead for the session.
6. **Every page view failed.** Sibling fields resolve in parallel, and the
   DataLoaders behind them shared the request-scoped `DbContext` — six fields at
   once returning "a second operation was started on this context instance".
   This one reproduces *only* against a real database: in-process SQLite answers
   each batch fast enough that the dispatches never overlap, which is why its
   regression test lives in the §14 container tier.

The remaining five were AppHost wiring (stock SQL Server image with no full-text
search and no `vector` type; unresolved optional connection strings holding
the API in a pending state forever; no `WaitFor` on the database, so
migrate-on-startup lost a boot race; a Keycloak reference injecting service
discovery variables while the API read a connection string; MinIO running with
nothing configured to use it), and one was a test that passed only while the
application was *mis*configured. All twelve are fixed, with regression tests
where the defect was in code.

Continued probing of the same running stack turned up a thirteenth the next
day, of exactly the same shape: **an unauthenticated GraphQL query for any
audited list crashed instead of answering.** Every read root gives an anonymous
caller the empty, absent-shaped answer, and `DbAuditSink` refuses to write a row
with no acting user (§7) — both correct, and nothing between them established
that an anonymous request has no row to write, so `AuditFieldMiddleware`
dispatched a success row for the non-null empty list and the sink threw. Eleven
root fields answered "Unexpected Execution Error". The §14 SQLite tier could
always have caught this — its fake handler authenticates a request with no
claims header as genuinely anonymous — but no test had ever aimed an anonymous
client at an audited *list*, only at `me` and the unaudited roots.

The general lesson is worth keeping: **every one of these was a seam between
two correct components** — Keycloak and its import format, ASP.NET and its
claim mapping, the AWS SDK and its transport, Aspire and the API's
configuration. Unit and integration tests own the components; only running the
system owns the seams. What remains SQL-Server-only and unbuilt is narrower:
the DiskANN vector index (deliberately deferred with engine-verified,
tripwire-tested blockers — §9.3) and the audit partitioning/append-only
grants, whose DDL is not yet written (§7, §14). The k3s deployment (milestone
9) is the largest remaining unexercised seam.

---

## 17. Open questions

- [ ] Expected scale? (users, pages — affects whether SQL Server FTS is enough)
- [x] Page URLs — resolved: both, with `/spaces/{spaceKey}/{slug}` as the
      address people see and `/pages/{id}` kept working for anything holding
      only an id. The hierarchy is deliberately *not* in the URL, which is what
      makes the pretty form stable: a page can be moved anywhere in its space's
      tree and every link to it still resolves. The cost is that slug
      uniqueness widens from (space, parent) to (space) — enforced by a
      filtered unique index over live pages, so a trashed page's address
      returns to the pool (and restoring re-checks it, since someone may have
      taken it meanwhile). Renaming a page does not touch its slug, and no
      mutation carries one after creation — the slug is immutable, which is why
      there is no redirect problem to solve at all.
      Collisions with the SPA's own space routes are structurally impossible
      rather than policed by a list: every system page for a space lives under
      a `-` segment (`/spaces/ENG/-/admin`, `/-/grants`, `/-/trash`), so `-` is
      the single reserved slug. A per-route word list (admin, grants, trash, …)
      would have had to grow in lockstep with the router, and forgetting to
      grow it would silently shadow every page already using that word.
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
- [ ] Which attributes beyond nationality? (employer/contractor status?) Each
      needs a Keycloak attribute + protocol mapper. Clearance is *off* this
      list: it was built as one and removed on 2026-09-04, because this
      deployment's Keycloak carries no such attribute (§21.12 records the
      reversal). Bringing it back means bringing back a gate, not adding a
      registry row.
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
      dependency. Scale-out is a deliberate future chart change — a Redis
      backplane AND a claim protocol for the embedding job (§15), together —
      not a values tweak.
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

Every page carries a **protective marking**, in four parts: an optional national
*prefix* (`UK`, or none), a UK Government *classification*, zero or more
*additional selectors* (at most one value per configured category, §21.15), and
an optional *eyes-only* caveat naming the countries the page is releasable to —
together, `UK SECRET APPLE NORTH AUS/NZ EYES ONLY`.

Two of the four gate access: the selectors (§21.15) and the caveat (§21.4).
The prefix does not, at all, ever, and since 2026-09-04 neither does the
classification (§21.12) — this deployment carries no clearance attribute for a
level to be compared against, so the level says what the content *is* and
decides nothing about who may read it.

**The parts that gate, enforce.** A selector or a caveat is not a banner the
author draws and the reader respects; it gates who can view the page, on every
read path, exactly as a page restriction does. That is the reason those parts
exist, and every other decision in this section follows from it. The
classification is the honest exception: a statement, carried and rendered with
the same rigour, whose whole job is to be read.

### 21.1 The scheme

Fixed, ordered, and not configurable:

```
OFFICIAL  <  OFFICIAL_SENSITIVE  <  SECRET  <  TOP_SECRET
```

The ordering is the **display order**, and the order §21.13's aggregate takes
its maximum by — and until 2026-09-04 it was the access comparison too: a
principal could view a page when their clearance was at or above its level.
That comparison is gone (§21.12 records the reversal); the ordering stays,
because a picker lists the four in scheme order and a compilation is marked at
the highest level of its sources, and both are facts about the scheme rather
than about any reader. So the four values remain hard-coded as a
`ClassificationLevel` enum with load-bearing numeric values (`tinyint` in the
database, so the maximum is numeric on every provider). The scheme is set by
policy, not by an admin, which is precisely what makes hard-coding it safe:
there is no fifth level to insert, and a member inserted in the middle would
silently re-rank everything below it. Numbering starts at **1**, so
`default(ClassificationLevel)` is not a valid level and an uninitialized value
can never read as OFFICIAL.

Three spellings of a level exist and they are deliberately different things: the
**wire name** (`OFFICIAL_SENSITIVE`) is what the sync payload, the audit
`DetailsJson` and the GraphQL enum all use (and what the retired `clearance`
claim used); the **display name** (`OFFICIAL-SENSITIVE`, `TOP SECRET`) is the
UK Government's own written form and appears only in the rendered marking; the
**reason token** (`official_sensitive`) was the spelling denial reasons
carried, and no denial reason names a level any more (§21.8) — the method
survives, called by no gate, so that the spelling still has exactly one home
if anything ever needs it. One method each, in `ProtectiveMarking`, so they
cannot drift.

"So they cannot drift" is only true if clients can *get* the display name, and
two surfaces need a level's spelling on its own rather than a whole marking — a
one-word list badge, and the picker that offers a level before one is chosen. Both
are served from the server, from the same `ProtectiveMarking.LevelName`:

- `PageMarkingView.levelName` — the level of a marking the caller already holds.
  Note it is **not** interchangeable with `label`: `label` is the whole marking
  (prefix, level, selectors, caveat) and is what anything claiming to show "this
  page's marking" must render. Rendering `levelName` in its place drops the
  selectors and the caveat, which understates the marking. That is the safer
  direction of error — both are still *enforced*, the badge is informational —
  but it is still wrong, which is why the two fields are named to be hard to
  confuse.
- `Query.classificationScheme` — every level in **scheme order** with its display
  name, for the picker, which has no marking in hand and offers all four to
  every editor (§21.6). The list order is the scheme order, so a client never
  encodes that OFFICIAL sorts below SECRET. Deliberately no numeric rank: the
  only use for one is comparing levels client-side, and the level is compared
  against nobody (§21.12) — a rank would be an invitation to invent a
  comparison the server does not make.

Without those two fields a client hard-codes four spellings and their order, which
is exactly the second implementation this paragraph exists to forbid — and the
order it would duplicate is the one §21.13's aggregate label takes its maximum by.

**The label grammar** is one token stream, single-spaced, with no brackets:

```
[UK ]LEVEL[ SELECTOR …][ A/B EYES ONLY]
UK SECRET APPLE NORTH AUS/NZ EYES ONLY
```

Selectors render as their **values only**, never the category name, in the
catalog's configured order (§21.15); the caveat's countries in canonical order
joined by `/`. A compilation's several caveat sets are listed comma-separated
(§21.13). The brackets the caveat once wore were dropped with the fixed
vocabulary (§21.4): with selectors between the level and the caveat, the label
is a sequence of tokens a reader scans left to right, and the comma is the one
separator that survives without a second `EYES ONLY` reading as one set. One
formatter, `ProtectiveMarking.FormatLabel`, renders every label in the system
(§21.13); nothing composes one from parts.

### 21.2 It composes by subtraction, and only by subtraction

Effective view access becomes:

```
canView(page) = S: some access grant in the space matches                 [§6.4]
                AND the page's marking row exists (not FailClosed)        [§21.5]
                AND G: every selector granted by a matched access grant   [§21.15]
                AND N: caveat empty, or nationality ∩ caveat ≠ ∅          [§21.4]
                AND R: every view-restriction on page + ancestors passes
```

The marking's gates are the availability check, G and N together, and
`MarkingGate` is their composition — the **one** entry point every marking
check in the system goes through: the calculator, the tree walk, the
self-lockout check when a marking is set (§21.6), the inspector's
ancestor-title withholding (§6.6), and the notification fan-out.
A source sweep pins that nothing outside Core calls `CaveatGate.Check` or
`SelectorGate.Check` directly — and inside Core only the composition and the
gates themselves do — so a new call site cannot check the caveat and forget
the selectors.

**Two rungs came out of this ladder on 2026-09-04, and that reverses what
this section argued for at length.** The ladder read S, C, E, G, N, R: C
compared the principal's `clearance` attribute against the page's level, and
E required a per-category Keycloak claim to say `yes` before the space's grant
was even consulted. Both were removed — not defaulted, not parked behind a
flag — because this deployment's Keycloak carries neither attribute. A gate
that reads a claim nobody emits has two possible behaviours, and both are
wrong. Deny everyone: E, as built, made every category that named a claim
eligible to nobody, and C without a floor would have emptied every page above
OFFICIAL for every user — an outage wearing a security badge, which pressures
whoever is on call into switching the control off. Or default everyone: the
OFFICIAL-SENSITIVE floor the retired §21.3 spent a page justifying, which on
an instance with no mapper at all is not a floor but the whole population's
permanent value — a comparison that partitions nobody, a control in name
only. A control whose input does not exist is not a control, and keeping the
code so that it "would work if the attribute arrived" is exactly the dormant,
untestable branch this document refuses everywhere else. The old reasoning
was not wrong about clearance in general; it assumed an identity provider
that states clearances, and this one does not. The level is presentational
now, like the prefix, and §21.12's argument covers both; selectors gate once,
through the space's grants (§21.15). If a future deployment does carry a
clearance attribute, the gate comes back as a gate — with the floor argument
re-read for that deployment, not resurrected.

**What replaced C at the front of the ladder is not a gate on the marking's
content but on its existence.** `ProtectiveMarking.FailClosed`, the stand-in
for a page whose marking is missing or recorded as unknown (§21.5, §21.10),
used to deny by *being* TOP SECRET — the level was the gate, and TOP SECRET
sat above every clearance. With the level presentational, a TOP SECRET
marking with no selectors and no caveat would deny **nobody**: the bug that
lost a page's marking row, or the sync import that had none to apply, would
have quietly made that page readable by everyone with space access, the exact
inversion of what "fail closed" means. So "marking unknown" is a distinct
state carried by an explicit flag, `IsUnavailable`, never inferable from the
level: true on the sentinel the read side substitutes for a missing row, and
on the sentinel a row recorded as unknown reads back as. `MarkingGate` checks
it first and denies everyone with its own reason, `marking:unavailable`
(§21.8), before any selector or caveat is looked at. A real page legitimately
marked TOP SECRET reads `false` there and is readable by whoever its selectors
and caveat admit. §21.5 has the rest.

**Nothing grants around it.** An access grant, a passing page restriction, an
`editor` or `space-admin` role grant, the instance `admin` role — none of them
widens a marking, in the same way and for the same reason none of them reads
around a page restriction (§6.5). This is structural rather than remembered:
the check lives inside `EffectivePermissionCalculator`'s one gate walk, the
computation every read path already funnels through, and there is no
parameter, overload, or flag by which a caller can obtain a `canView` that
skipped it. `canEdit` is reached only by falling through `canView`, so an
editor who is not granted a page's selector loses edit too without the gate
knowing edit exists. Note that G's input — the union of selector values over the access
grants the principal matches — is computed by the same walk that decides S,
so a grant can only ever contribute values to a principal it also admits; a
role grant contributes nothing to either.

The single loader in the data layer (`PermissionContextLoader`, §6.7) supplies
the marking, both grant kinds with their selector rows, the restriction chain
and the selector catalog together, so a call site cannot supply "no marking"
any more than it can supply "no restrictions". Its batched path loads every
marking **with its country and selector rows** in one query for the whole
batch — search post-filters hundreds of candidates, and an N+1 sitting on the
hot path of the control itself would be the thing that gets the control turned
off.

Ordering inside the computation — S, then availability, G, N, then R —
affects only which reason a denial *reports*, never the verdict: every gate is
a conjunct. S is reported first because "this principal may not see anything
in this space" is the whole answer, and because it is what decides whether the
marking is disclosed at all (§6.7). Availability comes next because there is
nothing to evaluate G or N against when it fails, and the marking gate then
reports that single entry in both forms rather than listing vacuous selector
and caveat passes. The marking
gates are reported ahead of a failing restriction because "this principal has
no business reading this page at all" is the more actionable answer for a
reviewer, and because they are the cheaper checks, so a page whose selectors
the caller is not granted costs no rule evaluations. `Compute` stops at the
first failure; `Explain` (§6.6) walks the
same ladder with short-circuiting off — one walk, one flag, so the order the
placeholder lists gates in (§21.8) and the order the audit row names one in
cannot drift apart.

### 21.3 Clearance, and every fail-closed choice in it — retired 2026-09-04

This section is left as a stub rather than renumbered away, in the document's
habit of recording a reversal where it happened. It described the clearance
gate: `clearance` as a well-known principal attribute holding a level's wire
name; the OFFICIAL-SENSITIVE floor an absent or unrecognised claim resolved to
(argued, at length, as the deliberate middle between "no clearance sees
everything" and an outage that gets the control switched off); closed ordinal
parsing of the four wire names; the highest-recognised rule for a multi-valued
claim; and the normalization of an undefined stored level to TOP SECRET,
because a `0` compared below every clearance and would have opened the page to
everybody.

The gate is gone, and with it the attribute, the floor, the multi-value rule
and the SPA's affordance ladder (`web/src/markings/clearance.ts`, deleted).
§21.2 records why: this deployment's Keycloak carries no clearance attribute,
and a gate on a claim nobody emits is either an outage or a default
masquerading as a control. The floor argument in particular does not survive
re-reading in that light — it was a good answer to "what should an
unconfigured mapper be worth" on an instance that *had* a mapper to configure,
and here it would have been the whole population's permanent value. What was
still load-bearing moved rather than vanished:

- **The closed parser** lives on as `ProtectiveMarking.TryParseLevelWireName`,
  because the sync importer still needs it (§21.10): a bundle may not smuggle a
  level in through `Enum.TryParse`'s tolerance of `"4"` or the C# member
  spellings, whether or not the level gates.
- **Normalizing an undefined stored level to TOP SECRET** stays in
  `ProtectiveMarking.Create`, no longer as an access-bypass fix but as an
  honesty one: a marking is a statement about what the content is, and one
  that rendered as nothing, or dragged a §21.13 aggregate down to an undefined
  minimum, would misstate it on every surface. The naming methods still answer
  TOP SECRET rather than throw for an undefined value, for §6.7's reason — a
  500 on a read path is distinguishable from a not-found.
- **The level-0 trap** — a value that reads as "nothing here" when it should
  read as "everything here" — did not go away; it moved to the missing-row
  sentinel, which used to deny by being TOP SECRET and now denies by an
  explicit flag (§21.5). §21.13's empty-intersection argument cites it by that
  description.

Nothing else in this section is current. The level is presentational, like
the prefix (§21.12); `me.clearance`, `UserProfile.clearance`,
`GateResult.requiredLevel`, the `CLASSIFICATION` gate and the
`classification:{level}` reason token no longer exist (§8, §21.8).

### 21.4 The eyes-only caveat

A marking may carry a **set of countries**; a principal must hold at least one
`nationality` value in that set. An empty set means no caveat. Absent or empty
nationality **denies** any page carrying one — fail closed, consistent with
`AttrCondition`: a principal with no value for an attribute matches no condition
that tests it, and the caveat is a condition on nationality.

**The vocabulary is fixed: `AUS`, `CAN`, `NZ`, `UK`, `US`.** This reverses an
earlier decision, and the reversal is recorded rather than quietly made. The
first version drew the vocabulary from the registered `nationality` attribute's
`AllowedValuesJson` (§6.2), so that the marking side and the token side could
not disagree about whether the United Kingdom is `GB` or `UK` — a mismatch that
fails *closed*, silently, with the page invisible to everybody including the
audience it names while the marking reads as perfectly correct in the admin UI.
That argument was right about the failure mode and wrong about the cure. It
guarded against *two admin-editable sides drifting* by making both of them
admin-editable, which is a wider door than the one it closed: a registry row
edited at runtime could widen or empty every caveat in the estate with no
deploy and no diff to review, and an instance that had not registered the
attribute could not set a caveat at all. The policy the caveat implements is a
fixed one — Five Eyes releasability — and a policy-fixed vocabulary belongs
where the classification ladder is (§21.1): hard-coded, in one place
(`NationalCaveatVocabulary`), and pinned in the GraphQL input enum, the SPA
build and the realm contract (§11.5) so that drift breaks a build rather than a
comparison. With one vocabulary on the marking side, the only failure left is a
mapper emitting a foreign token, and canonicalisation turns that into an
**empty** nationality — every caveated page denied, the emptiness visible in
`me.nationality` — rather than a silent partial match. It is a deployment
defect caught at first login, not a representable state. The `nationality`
registry row, where one exists, is rule-builder vocabulary for `attr`
conditions only (§6.2).

**Canonical form.** Country values are stored and compared **upper-cased,
trimmed, de-duplicated and ordinally sorted**. Canonicalizing in one place means
the stored rows, the display string, the audit `DetailsJson` and the sync payload
all agree byte-for-byte, and a set that round-trips through sync comes back
identical rather than merely equivalent. The value object keeps an unknown
token rather than dropping it — a legacy `GB` row, or a bundle from an instance
with its own idea of the vocabulary, stays on the marking and **matches
nobody**, which is the fail-closed reading; the mutation refuses one with a
`ValidationError` naming the fixed set; and the principal side ignores unknown
claim values — a foreign token holds nothing rather than poisoning the values
beside it, so garbage can never widen what a principal holds.

That upper-casing is a **documented, deliberate departure from §6.3's "matching
is exact (ordinal), no case folding"**, confined to the marking comparisons
(the selectors of §21.15 share it). §6.3 keeps rule matching ordinal because an
admin hand-typing a group name should not have a typo silently forgiven. Here
the two sides come from different systems that were never guaranteed to agree
on case — a fixed vocabulary and an OIDC claim mapper — and a case mismatch
would deny every legitimate reader while looking correct. Failing closed on a
casing difference is not security, it is an outage. The rule engine's `attr`
conditions are untouched.

**Rendering** has exactly one implementation, server-side
(`ProtectiveMarking.Format`, exposed as `PageMarkingView.label` in GraphQL), so
the SPA, an MCP client and an audit reviewer all read identical text. Two
renderings of one marking that disagree is a compliance problem, not a cosmetic
one. The caveat renders after the level and selectors with **no brackets**:
`SECRET UK EYES ONLY`, or `SECRET UK/US EYES ONLY` for several countries in
canonical order (the grammar is §21.1's). The tokens are the fixed set's own,
and the old question of aliasing `GB` to `UK` for display dissolves — there is
no `GB` to alias. Legacy `GB` rows were remapped to `UK` by the
`AddMarkingSelectorsAndFixedCaveat` migration (the same nation, so not a
widening); any other legacy token is left in place, matching nobody, with the
review query in the migration's comment.

### 21.5 Every page is marked

There is **no unmarked state**. A page's marking is created with the page,
inheriting its parent's — level, selectors and caveat alike (root pages start
at `UK OFFICIAL`) — and an editor may override it afterwards **in either
direction**.

Inheritance happens **once, at creation**, producing a value the page then owns —
it is not re-derived from ancestors at read time the way restrictions accumulate
(§6.4). So a child may legitimately sit above *or below* its parent, and an
editor changing a parent's marking does not silently re-mark the subtree. The one
place that asymmetry shows is the page tree, which renders a node the caller
fails a gate for as a `(protected)` leaf (§21.8) **and does not descend into its
subtree**, including children the caller *could* see: a tree cannot render a
node whose parent is a placeholder, and the more-hidden direction is the safe
one. Such a child stays reachable by id and through search, both of which check
it on its own.

The invariant is enforced at the **persistence seam**: `RocketWikiDbContext`
materializes an OFFICIAL marking for any `Page` being inserted without one —
or its parent's, when the parent is in the same unit of work, and that
includes the parent's *unavailability* (§21.10): a child that read as OFFICIAL
beneath a parent nobody can read would be exactly the widening the seam exists
to prevent — so an
unmarked page cannot be committed through any code path — present or future —
that goes through the context. This is the write-side twin of putting the
marking gate inside the calculator: an invariant that lives in one structural
place cannot be lost one call site at a time. It is a backstop, not the feature —
`PageService` sets the marking explicitly with real parent inheritance, and the
sync importer sets it explicitly too — and it touches the database not at all, so
it costs nothing on every write.

**A page found at read time with no marking row is readable by nobody.** Belt
and braces against a future code path that forgets: the read side substitutes
`ProtectiveMarking.FailClosed`, and the substitution has exactly two
implementations (`PermissionContextLoader` and its batch sibling) so no
consumer ever holds a nullable marking it could decide to ignore. Note the
asymmetry with the insert-time default, which is intentional: a missing
marking on *read* means something went wrong, and the answer to that is
"nobody, until somebody puts the row back"; a missing marking on *insert*
means nobody said, and defaulting that to a refusal would lock a page's own
author out of it.

**What denies is a flag, not the level — the subtle consequence of the level
becoming presentational (§21.12).** `FailClosed` used to deny by *being* TOP
SECRET: the level was the gate, TOP SECRET sat above every clearance, and
"treated as TOP SECRET" was a complete description of the behaviour. Once the
level is compared against nobody, a TOP SECRET marking with no selectors and
no caveat denies **nobody** — so the same sentinel, unchanged, would have
turned "a bug lost this page's marking row" into "everyone with space access
can read it": fail-*open*, silently, while every label on every surface still
said TOP SECRET. That inversion is why "marking unknown" is now a distinct
state, `ProtectiveMarking.IsUnavailable`: true only on the one `FailClosed`
instance — which the read side substitutes for a missing row, and which a row
*recorded* as unknown reads back as (`IsUnavailable` on `PageMarkings`,
`AddMarkingUnavailableFlag`: set by the sync importer for a
payload that carries no usable marking, cleared by any write that states a
real marking, §21.10) — never on anything `Create` builds, and part of
equality so the sentinel is never equal to a real TOP SECRET
marking. `MarkingGate` checks it before anything else and denies with its own
reason, `marking:unavailable` (§21.8), which reaches the wire as the
`MARKING_UNAVAILABLE` gate and the metric as `marking-unavailable` (§15) — a
diagnosis addressed to whoever restores the row, not to the reader, which is
why the SPA says the same sentence to everyone. The sentinel still *renders*
as a bare `TOP SECRET` where the application talks to itself — it is the value
a §21.13 aggregate must take when a missing row is among its sources, and the
right visual signal in a log or an admin screen that something is wrong — but
that string is **never disclosed to a reader**. A denial for this gate
withholds the marking entirely (`AccessDenialView.From`), exactly as a denial
for a missing space-access grant does. The reasoning is the one that already
denies the sentinel a prefix, applied to the level now that the level has no
force: telling somebody "this page is TOP SECRET" when the row says we do not
know what it is asserts a classification nobody made, about content nobody has
reviewed. While the level still gated reads, that string was at least a fair
summary of the consequence; it is not one any more. It carries **no
selector and no caveat**, for the same reason it carries no prefix (§21.12):
the flag already refuses everyone, and a sentinel selector or country would
put a token into enforcement that nobody configured. The general lesson is
worth stating: a fail-closed default that works by *being the strictest value
on a scale* stops working the moment that scale stops being compared, and it
fails in the quiet direction. Pinned by test — the calculator denies a
missing-row page to a space admin holding every selector, and a real TOP
SECRET page with no selectors and no caveat is readable with space access.

### 21.6 Changing a marking

`setPageMarking` replaces the whole marking — level, prefix toggle, selectors
and country set together. A marking is one value; a partial update would let a
caller change the level without ever stating what caveat or selectors they
meant. The input is `{ pageId, level, ukPrefix, selectors, eyesOnly }`, every
field stated, none defaulted.

- Requires **`canEdit` on that page**, beneath the replica invariant (§12), which
  refuses first and beneath every grant. `canEdit` already includes the marking
  gate against the page's *current* marking, so a page you cannot see is a page
  you cannot re-mark — including re-marking it downward to make it readable,
  and including a page whose marking is recorded as unknown (§21.10), which
  nobody can read and therefore nobody can re-mark here; a declared marking
  from its origin is the only way out, by construction rather than by rule.
- **The input must be well-formed against the vocabularies**, else a
  `ValidationError` naming the offending part: a level outside the ladder, a
  country outside the fixed set (§21.4), a selector whose category or value is
  not configured (§21.15), or two values in one category. At the GraphQL edge
  the caveat is typed as the `NationalCaveatCountry` input enum, so a country
  outside the set never reaches the service from there — it is a schema
  validation error (HTTP 400); the service's own check stays for every other
  caller.
- **You may not set a marking you could not then read.** Enforced as the
  resulting marking *as a whole* — each selector's grant, and the caveat,
  checked through `MarkingGate` against the caller's own nationality and the
  selector values their access grants confer *in this space* — because that is
  what mechanizes the stated reason: marking a page `APPLE` when no access
  grant you match carries `APPLE`, or `SECRET US EYES ONLY` as a UK-national
  editor, loses you the page. Refused as a `ForbiddenError` whose message is
  the gate's reason token — the input is well-formed, the caller is simply not
  entitled to the result. **The level is unconstrained**: any editor may set
  any level, because the gate does not read it (§21.12) and there is no level
  a caller could lose a page by choosing. Until 2026-09-04 "a level above your
  clearance" was the first item in this list; it went with the clearance gate
  (§21.2), and it went *for free* — the rule is `MarkingGate.Check` on the
  resulting marking, so when the gate stopped reading the level the constraint
  stopped existing, exactly as §21.12 says of the prefix.

**The UI prevents rather than refuses, and that is affordance data, not
authorization.** `me.nationality` echoes the caller's own resolved
nationality, and `Space.viewerSelectorGrants` the union their access grants
confer there, so the marking picker can grey out a value not granted to them
in this space and warn about an eyes-only set that excludes their own
nationality, instead of offering a choice the server will reject. Every level
is offered to everyone: there is nothing about the caller a level could be
greyed out against. (`me.clearance` and `me.selectorEligibility` used to feed
a greyed-out level and a greyed-out category; both fields went with their
gates, §8.) Three properties make the affordance safe rather than a second
access-control implementation:

- **It is the caller's own token, echoed back.** Same category as `groups`, which
  `me` already returned; it discloses nothing the caller did not present. The
  grants field discloses only what the caller already holds.
- **The nationality resolves through the gate, not the raw claim** —
  `CaveatGate.ResolveNationalities`, the same path enforcement uses, so the
  values are *canonicalized* against the fixed set. The canonicalization is
  load-bearing: a marking's country set is always canonical, so a token saying
  `uk` is admitted to a `UK` marking by the server, and a client comparing
  against the raw claim would have concluded the opposite and warned the
  author out of a marking that would have worked. It is §21.4's case-mismatch
  trap one layer up, closed the same way — one canonicalizer, both sides; the
  SPA folds selector tokens the same way before comparing them with the
  grants.
- **The server decides regardless.** `PageMarkingService` re-checks the resulting
  marking through `MarkingGate` and returns a typed error; a stale, spoofed, or
  simply wrong client-side comparison changes nothing but the polish.

**Downgrading is permitted but audited distinctly.** A change is a *downgrade*
when it makes the page readable by someone it was not readable by before, or
lowers what the content is declared to be: the level drops, the caveat is
cleared, the caveat gains a country it did not admit, **a selector is removed,
or a selector's value is swapped within its category**. The level is listed on
purpose even though lowering it moves no access line on this deployment
(§21.12): a declassification is still the judgement a reviewer's
`page.marking.downgrade` query exists to find, wherever the content is later
read, and dropping it from the definition would hide exactly that. Swapping
`{UK}` for `{US}` counts, even though UK also loses access
— somebody who could not read the page yesterday can read it today, which is
the fact a reviewer is looking for; swapping `APPLE` for `BANANA` counts by the
same reasoning, since it removes `APPLE` and everybody granted `BANANA` but not
`APPLE` can read the page today. Adding a selector never is one, and neither
is the prefix toggle (§21.12). Erring toward "call it a downgrade" is the safe
error: the cost is one extra row in a reviewer's result set, and the cost of
the opposite error is a widening nobody sees.

### 21.7 Audit

Two actions, one domain event, through the pipeline (§7) so the row commits in
the same transaction as the change:

| Action | Subject | Details |
|---|---|---|
| `page.marking.set` | `page` | `{ level, eyesOnly, selectors, prefix, previousLevel, previousEyesOnly, previousSelectors, previousPrefix }` — `selectors` an object keyed by category, `{"FRUIT":"APPLE"}`, the sync payload's shape |
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
is also the only place the country set and the selector values are written out
in full: §15 keeps both out of every telemetry tag, and the denial reasons
deliberately name no country and no value (§21.8).

A page's *creation* raises no marking event: the inherited value is deterministic
from the parent, which the `page.create` row already identifies, and a
`page.marking.set` row beside every `page.create` would be noise that made real
marking changes harder to find.

### 21.8 Denial is disclosed — where, and where not (§6.7)

What a caller who may not read a page is told depends on the surface, and the
boundary is drawn once, here; the reasoning is §6.7's.

| Surface | A page the caller fails a gate for |
|---|---|
| `pageAccess(id)` / `pageAccessBySlug` | **disclosed**: a `(protected)` placeholder carrying the marking label and every failing gate; `null` only for a page that does not exist |
| `pageTree` / `pageSubtree` / `PageTreeNode.children` | **disclosed**: a `ProtectedTreeNode` leaf in its sibling position, never descended; the whole tree is empty when the caller holds no access grant in the space |
| `Page.linkTargets` | **disclosed**: a placeholder per denied target of the page's own `page://` links; a missing target has neither page nor denial |
| `Page.parentDenial` | **disclosed**, beside the unchanged null `parent` |
| `page(id)` / `pageBySlug` | `null`, byte-identical to a page that does not exist — pinned, so the plain read stays leak-free |
| search, Ask-the-wiki, RQL and page lists, label listings, home feeds, notifications, `Page.children`, comments, attachments, revisions, watch state, SignalR joins, space listings, the inspector, every MCP tool | **omitted**: no placeholder, no gap, no count — and a sweep pins that the placeholder vocabulary never appears in any of their responses |
| the document graph — `pageGraph(spaceKey)`, `Page.inboundLinks` / `outboundLinks` and their counts | **omitted**: no node, no edge touching it (an edge exists only when the caller can view both endpoints), and no count that includes it — a count is the length of its filtered list by construction. A space the caller cannot enter, an archived space and a key naming nothing are the same empty graph, byte-identical. The read audits as `graph.view`, always `success` (§7); the per-page fields are a listing continuation of the already-audited page read, exactly as `linkTargets` is. Pinned by the same sweep |

**A placeholder carries the marking and the reasons, and nothing that
identifies the page.** Its title is a server constant; its marking is the
page's full label (level, selectors, caveat, prefix); its reasons are every
failing view gate with the detail each already implies once the marking is
shown — the selector's category and value, the caveat's countries, a failing
restriction's rule id and whether it is inherited; nothing for the
availability gate, which has nothing about the page to add. Never
its id, real title, slug, timestamps, author, labels, child count, subtree, or
a rule's expression (which says who *can* read the page — information about
the protected audience, withheld on the same reasoning as §6.6's title rule).
The tree placeholder is a leaf; the subtree beneath it does not exist to the
caller. The allowlist of fields is pinned by an introspection test, so a field
added to the placeholder later fails the build until it is argued for here.
Two details of the gate rows follow from what each surface holds: on a tree
placeholder `inherited` is false by construction — an ancestor whose rule the
caller fails is itself the placeholder, and nothing beneath it is walked,
so a placeholder's failing restrictions are its own — whereas `pageAccess`,
`linkTargets`, `parentDenial` and the inspector compute it from the subject
page's id; and the country set a caveat row carries comes from the marking in
hand, so on the inspector's `viewGates` it is null, the page's own `marking`
sitting beside it (the `requiredLevel` a classification row once carried went
with that gate, §8). The list itself holds
only *failed* gates — `passed` is always false inside a denial — and is
meaningful as pass/fail only on the inspector's `viewGates`/`editGates`.

**No access grant: only the space sentence, and the marking is withheld.** When
S fails (§6.4) the placeholder says that the caller has no access to this
space, lists that one gate, and carries no marking. The other gates are still
*evaluated* — the inspector needs them — but the denial record itself (`PageDenial`,
built in Core) carries only that one gate and no marking, so no API projection
can list more, since
the level and selectors of a page in a space the caller may not enter are the
census this section refuses. A space the caller holds no grant of any kind in
is not listed at all; one they hold only a role grant in is listed with an
empty tree, because its administrator has to reach its settings (§6.5.2).
Stated because it was weighed: `pageAccessBySlug` gives that same placeholder
for a page in a space the caller cannot enter, so `(spaceKey, slug)` confirms
that a page exists at that address — the bounded space-key oracle accepted
with the "space denial only" choice, and all it confirms; the marking, title
and id are withheld exactly as by id.

**Reasons follow one vocabulary**, and the same token is the audit row's
`reason`, the placeholder's gate, the inspector's entry and the mutation's
self-lockout message:

| Reason | Gate | Meaning |
|---|---|---|
| `no-space-access` | S | no access grant in the space matches the principal |
| `marking:unavailable` | availability | the page's marking row is missing; nobody reads it until it is restored (§21.5) |
| `selector:unknown:{CATEGORY}` | G | the category is not configured on this instance (§21.15) — a misconfiguration, not a missing grant |
| `selector:not_granted:{CATEGORY}` | G | no access grant the principal matches carries the page's value |
| `caveat:eyes_only` | N | the eyes-only set and the principal's nationalities do not intersect |
| `restriction:{pageId}:{ruleId}` | R | the named restriction fails (§6.7) |
| `replica-read-only` | edit: replica | the space is a replica (§12); never in a placeholder, which lists view gates only |
| `insufficient-space-role` | edit: role | no role grant of `editor` or above matches (§6.4); likewise edit-only |

The audit row names the **first** failing gate in the order S, availability,
G, N, R; the placeholder lists them all. Two tokens this table no longer has:
`classification:{level}` (C) and `selector:not_eligible:{CATEGORY}` (E) went
with their gates on 2026-09-04 (§21.2) and are never minted — an audit row
carrying either predates that day. A selector token names the **category, never
the value**, and the caveat token names **no country**, deliberately: category
names are bounded configuration vocabulary like a level, whereas a value is the
compartment's codeword and a country set is the marking's content, and a reason
string carrying either would put it one careless tag away from a metric
dimension. The structured gate result carries the value where it is disclosed
on purpose (the placeholder, the inspector); the token does not.

**Telemetry (§15).** `CoreTelemetry.CategorizeDenialReason` collapses
`marking:unavailable` to `marking-unavailable`, `caveat:eyes_only` to
`caveat`, and every `selector:*` token to a single `selector` — not per
category, not per value — and keeps `no-space-access` as itself. The
`classification` category went with its token; a stray one would land in
`other`, pinned by test, so no per-level series can reappear. The argument
that kept the level out of a tag while it *was* a gate still governs the
selector: a "denials by codeword" series is a census of the compartmented
estate and how hard it is being probed, published to whatever audience the
dashboard has, which is why a category name reaches a token but never a tag.
That is exactly the second, unregulated record of who-reads-what §15 exists to
prevent, and the audit table already holds the specific category and rule for
anyone entitled to ask.

### 21.9 Reach

Enforcement is inherited, not reimplemented, everywhere `canView` is already
computed through `PermissionContextLoader`: page reads, `parent`/`children`,
revision history, search (keyword and vector — the post-filter is the same
batch), Ask-the-wiki retrieval (a page the asker fails a marking gate for never enters
the prompt, not merely the citation list), the document graph (every candidate
page decided in one batch, and a node carries the marking that batch gated on,
the tree's carry-the-value rule below), MCP tools, attachments, comments,
labels, page properties, watch state, the notification read model, the co-editing
hub's join check, and the §6.6 permission inspector (whose non-short-circuiting
`Explain` is the gate's own walk with short-circuiting off, pinned by test to
agree with it — and which, because a chain names pages the caller may fail a
gate for, withholds such an ancestor's *title* while still explaining its rule;
see §6.6).

Two read paths assemble their own authorization inputs and therefore had to have
the gate added by hand. Both now have it; both are worth knowing about:

- **`PageReadService.GetPageTreeAsync`** walks a whole space in memory precisely
  to avoid per-node work, so it assembles its own inputs rather than asking the
  loader per node. It decides S once for the space (no access grant — an empty
  tree, §21.8) and then runs **the calculator's own per-node view evaluator**
  over each node's marking and own rules, non-short-circuiting, so a denied
  node becomes a `ProtectedTreeNode` carrying its complete failed-gate list and
  no title, and so the tree's verdict for a page cannot differ from
  `GetPageAsync`'s (pinned by a parity test over every page in a space). The
  marking, with its country and selector rows, is loaded on the same
  constant-query budget — one more query for the whole space.

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
  the loader at all. It builds the same `PermissionInputs` the calculator takes
  — both grant kinds with their selector rows, the page's marking with its
  rows, and the catalog — once per fan-out, and hands them to the same gate
  walk.

**The tree's carry-the-value rule is deliberately not applied everywhere.** Ask-
the-wiki and MCP search both *re-read* markings through `IPageMarkingReader`,
separately from the rows `PermissionContextLoader` consulted while filtering — the
opposite of what the tree does two paragraphs up, and it looks like an oversight
until you see why the tree is the exception. The tree hand-rolls its own
authorization to walk a space on a constant query budget, so the gated value is
already in its hand and re-loading it would be gratuitously weaker. On the search
and Ask paths the filtering happens inside `SearchService` and the display value is
fetched once for the whole batch afterwards; threading the gate's internal rows out
through the service boundary would widen a contract for a difference nobody can
observe. The window is one batched query wide, both reads are of the same row, and
a marking that changed inside it is reported by whichever of the two ran later —
which is no worse than the answer a request arriving one second later would get.
It is a consistent system-wide choice, made on purpose; the tree is the documented
exception, not the rule.

### 21.10 Sync (§12)

Markings **travel with content**. A page that is SECRET on low is SECRET wherever
it lands; letting the high side rediscover that for itself is the hole this
closes. This is unlike space grants, which stay local because the high side
decides who may read its replica — a grant is about *this instance's* people, a
marking is a property *of the content*.

- `SyncEventType.PageMarking` (10), payload
  `{ pageId, level, eyesOnly, selectors, prefix }` — the level as its **wire
  name**, never the tinyint, so a future renumbering cannot silently re-rank a
  bundle already sitting on a transfer disk. The prefix travels even though it
  gates nothing (§21.12): a replica must render the same marking string as its
  origin. An **absent** `prefix` key means *no prefix*, never this instance's
  default.
- `selectors` is an **object keyed by category** — `{ "FRUIT": "APPLE" }` —
  which makes "one value per category" structural on the wire rather than a
  rule the parser remembers. It is **always present on export** (`{}` when the
  page carries none), so an absent key unambiguously means a pre-selector
  bundle and lands with no selectors. A *malformed* `selectors` (not an object,
  a non-string value, an over-long or ill-formed token) makes the **whole
  marking** unparseable, which is the existing "unparseable level" rule
  extended: a new page lands in the recorded unknown state, readable by nobody
  (below); an existing row is left alone.
  Dropping only the bad selector and applying the rest would widen, which is
  the one direction sync never takes. An **unknown** category or value — one
  this instance has not configured — is well-formed and is kept verbatim,
  stored, and matches nobody (`selector:unknown:`, §21.8) until an operator
  configures it; that is §12's "unknown on high matches nobody", applied to
  selectors. This is what bumps the bundle to **format 3** (§12).
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
- The country set crosses verbatim. The sending instance may have used a token
  outside the fixed set (§21.4), and that is handled the §12 way rather than by
  dropping the caveat: an unrecognised country matches no principal, so the
  page arrives **more** restricted — exactly as "a group/attribute unknown on
  high matches nobody" already works for restrictions. Dropping it would be
  the one unsafe direction.
- **A page cannot land on the high side unmarked — and "unmarked" is a
  recorded state, not a guessed level.** A payload with no marking and no
  local row creates the row **unavailable**: `IsUnavailable`, a bit on
  `PageMarkings` (`AddMarkingUnavailableFlag`,
  data-model.md), copied by every writer from the value it persists and true
  on exactly one value, `ProtectiveMarking.FailClosed` — which is what the
  importer applies when the payload carries no usable marking. A row with the
  bit set reads back as that sentinel (§21.5), so the page is readable by
  **nobody** — every reader, admins included, gets the `(protected)`
  placeholder whose one reason is that its marking is missing
  (`marking:unavailable`, §21.8) — until a **declared marking arrives from
  the origin**, which clears the bit because the marking it is copied from
  was built by `Create`. Nothing on the high side clears it locally: a replica
  is read-only, and `setPageMarking` needs `canEdit`, which needs `canView`,
  which the unavailable gate refuses (§21.6) — so the remedy is the one this
  bullet exists to force, a reviewed marking on the low side that then
  crosses. A payload with no marking for a page the high side already holds a
  marking for leaves it alone (silence must never re-classify, in either
  direction). The unknown state itself crosses as *no marking*: an
  unavailable page re-exported, or included in a baseline, carries a null
  marking rather than the sentinel's parts, so it lands unavailable again
  instead of as a bare TOP SECRET that would gate nobody. An unparseable
  level reads as absent, not as OFFICIAL, and the parser is the closed one —
  `ProtectiveMarking.TryParseLevelWireName`, the inverse of `LevelWireName`,
  kept from the retired §21.3 for exactly this reason: only the four wire
  names parse, never `Enum.TryParse`'s member spellings or `"4"`.

  **Rows written before the flag existed are not backfilled, and that is
  stated rather than hidden.** The older importer wrote an unmarked arrival as
  a bare, prefix-less TOP SECRET with no actor — byte-identical to a genuine
  TOP SECRET marking an editor set with the prefix toggled off — so the
  migration cannot tell the two apart, and guessing would either lock a real
  page away from everyone or leave an old unknown readable. No instance is
  known to hold such a row; an operator inheriting a database from before
  `AddMarkingUnavailableFlag` runs the review query in the migration's
  comment, confirms each candidate against its origin, and either re-syncs a
  declared marking or sets the bit by hand.

  **Why a recorded state, and not "create the row at TOP SECRET".** That is
  what this bullet used to say, and it was a real control while the level
  gated reads: TOP SECRET sat above every clearance, so an unmarked arrival
  was visible to nobody but the highest-cleared. With the level presentational
  (§21.12) the same row would gate *nothing* — a persisted TOP SECRET marking
  with no selectors and no caveat reads back through
  `ProtectiveMarking.Create` as an ordinary, available marking, and the page
  would be readable by everyone the replica's access grants admit, wearing the
  scheme's strictest label the whole time. That is the fail-open §21.5
  describes, reached through the persistence seam instead of through a lost
  row, and it is why "unknown" had to become something the row itself records
  rather than something the read side infers from a level. The force of the
  refusal comes from the recorded state and from nothing about how high TOP
  SECRET is; the sentinel still *renders* as a bare `TOP SECRET`, for the
  display reasons §21.5 gives, and a reader who sees that label beside the
  missing-marking reason is looking at a page whose origin has not yet stated
  what it is.
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
would have made every page invisible to everyone below TOP SECRET the moment
the migration ran — the wiki would have locked itself out of itself, including
out of the admin pages describing how to fix it. The recovery would have been
a hand-written `UPDATE` against a production database, which is the one
operation this whole design exists to avoid. An access control that has to be
switched off to be adopted does not get adopted.

That lockout argument no longer applies to the *level* — since 2026-09-04 it
gates nothing (§21.12), so a TOP SECRET backfill would lock nobody out — and
the decision stands on its other leg: a marking is a statement about what the
content *is*, and stamping TOP SECRET on a back catalogue nobody has reviewed
would invent a fact on every page, the same objection §21.13 raises to marking
an empty answer. The state that *would* lock the wiki out of itself today is
the recorded "marking unknown" state of §21.10, and the backfill does not use
it for the same reason it never used TOP SECRET: "nobody said" on a page this
instance already held is OFFICIAL by decision, while "we do not know what it
said" is reserved for content that arrived without a marking. OFFICIAL is
still the pragmatic, unreviewed call, and the review sweep above is still
owed; what changed is that the risk is now purely one of misstatement, not of
access.

### 21.12 The national prefix

UK protective markings are conventionally written with a national qualifier —
`UK OFFICIAL`, `UK SECRET`, `UK TOP SECRET` — so a marking carries an optional
**prefix** alongside its level, selectors and caveat.

**It is a toggle: `UK`, or none.** The mutation takes `ukPrefix: Boolean!`, so
the only values that can be written are `UK` and `NULL`; GraphQL exposes
`ukPrefix` and the old free-text `prefix` field is gone. The value object and
the column keep a *string* (`nvarchar(16)`) underneath, for sync: a legacy
bundle's prefix renders verbatim rather than being reinterpreted as a boolean,
and `FailClosed` still needs a "no prefix" it can assert. The
`AddMarkingSelectorsAndFixedCaveat` migration nulled every stored value other
than `UK` — and by this section's own argument that needed no review sweep —
nothing about who can read a page moved.

**It is presentational, and that is a hard boundary, not a phase.** The prefix
has *no access-control considerations whatsoever*:

- `MarkingGate` does not read it, and neither `CaveatGate` nor `SelectorGate`
  beneath it does. The gates' entire input is the selectors, the eyes-only set
  and the availability flag; `ProtectiveMarking.Prefix` is never touched by any
  of them. (The proof was mechanical when the prefix arrived: that commit had a
  **zero-line diff** on the caveat gate — then `ClearanceGate.cs` — and on
  `EffectivePermissionCalculator.cs`; the selector gate was written after it
  and takes no prefix.)
- It never appears in a denial reason. The tokens of §21.8 are unchanged by it,
  so nothing about a prefix can reach an audit reason or — via
  `CategorizeDenialReason` — a metric tag.
- It changes no verdict. Pinned by test at two tiers: `CaveatGateTests` sweeps
  every caveat × principal combination and asserts the decision **and the
  reason** are byte-identical with and without a prefix, and `PageMarkingTests`
  repeats it through the real permission loader against a real database.
- It is outside "you may not set a marking you could not then read" (§21.6),
  and it falls outside *for free* rather than by exception: that rule is
  `MarkingGate.Check(resultingMarking, …)`, and the gate does not read the
  prefix, so there is no prefix a caller can be refused for.

**The classification joined it on 2026-09-04, and every bullet above now holds
for the level word for word.** This reverses the position this document held
from §21's first line ("it enforces") through §21.3, and it is recorded as a
reversal. The level was the C gate — `clearance ≥ level`, with a floor for the
claimless. It stopped being a gate because this deployment's Keycloak carries
no clearance attribute; §21.2 has the full argument for removing rather than
defaulting it. The consequence is that the level is now exactly what the
prefix has always been — a statement about the content, rendered with full
rigour, compared against nobody:

- `MarkingGate` does not read `ProtectiveMarking.Level`; the gates' input is
  listed above and the level is not in it. Level-invariance sweeps in
  `CaveatGateTests` and `MarkingGateTests`, and a sweep over the whole §21.15
  truth table, pin that every verdict and every reason is byte-identical at
  every rung.
- No denial reason names it: `classification:{level}` is never minted, the
  `CLASSIFICATION` gate and `GateResult.requiredLevel` are gone from the wire
  (§8), and `CategorizeDenialReason` has no `classification` category left to
  fall into (§15).
- Any editor may set any level (§21.6): the self-lockout rule is the gate on
  the resulting marking, and the gate does not read the level, so there is no
  level a caller can be refused for — the same "for free" the prefix gets.
  The picker offers all four to everyone.
- What the ordering is still for: the picker's display order and §21.13's
  aggregate maximum (§21.1), and the downgrade audit (§21.6), where a lowered
  level still counts because a declassification is a reviewer's fact whether or
  not this instance ever gated on it.
- What it forced elsewhere: the missing-row sentinel could no longer deny by
  being TOP SECRET, so it denies by an explicit flag (§21.5) — the one place
  the level's retirement needed new code rather than deleted code.

If you are reading this because you were about to give the prefix — or, now,
the level — access semantics "for completeness": don't. There is nothing to
compare either against. A principal has no "national prefix" claim and no
clearance claim; inventing the first would silently duplicate the nationality
attribute the eyes-only caveat already uses — with different values, a
different vocabulary, and no registry behind it — and inventing the second
would be a gate on a value every reader holds identically, which partitions
nobody (§21.2). Note also that the prefix and the caveat countries are
independent: `UK SECRET US EYES ONLY` is an ordinary marking, and reading the
leading `UK` as a releasability statement would be exactly backwards.

**It defaults to `UK`.** New markings, markings inherited from a parent, and —
via the `AddPageMarkingPrefix` migration — every row that predated the feature.
An instance whose content is not UK-marked switches it off per page; the
default is a default, not a policy.

**`NULL` is legal and must stay clearable.** Some content legitimately carries
no national qualifier, so the column is nullable and the toggle's "off" writes
`NULL`. That state renders the bare level with **no leading space** —
`SECRET`, not ` SECRET` — because a cosmetic gap would make two identical
markings compare unequal as strings, and the label is what the SPA, an MCP client
and an audit reviewer all read.

**Canonical on write**, exactly like the country values and for the same
reason: the stored row, the label, the audit `DetailsJson` and the sync payload
must agree byte-for-byte. The mutation can only produce `UK`; a sync-imported
value is trimmed and upper-cased as before, so `uk` in a bundle becomes `UK`
stored.

**The label is the one place it appears.** `ProtectiveMarking.Format()` — exposed
as `marking.label` — renders prefix, space, level, selectors, then caveat
(§21.1). The four prefix × caveat combinations, on a page with no selectors:

| Prefix | Caveat | Label |
|---|---|---|
| `UK` | `{UK, US}` | `UK SECRET UK/US EYES ONLY` |
| `UK` | none | `UK SECRET` |
| none | `{UK, US}` | `SECRET UK/US EYES ONLY` |
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
`DetailsJson` as `prefix`/`previousPrefix` (the string, so a legacy value a
bundle delivered is recorded as what it was), because a prefix change *is* a
change to the marking and a reviewer must be able to explain why a page's
rendered marking changed. It reaches **no** telemetry tag.

**It is not a downgrade.** `ProtectiveMarking.IsDowngrade` does not consult it:
a downgrade means somebody who could not read the page yesterday can read it
today, and the prefix cannot move that line in either direction. Clearing
`UK SECRET` to `SECRET` audits as an ordinary `page.marking.set`, with the
before-and-after prefix in the details. Counting it as a downgrade would dilute
the one query that exists to find real widenings.

**`ProtectiveMarking.FailClosed` carries no prefix**, unlike `Baseline`. That
value means "this page's marking is missing, or was recorded as unknown, and
we do not know what it said" (§21.5, §21.10), so asserting a national
qualifier on its behalf would be inventing a fact. It renders a bare
`TOP SECRET`, which is also a quiet visual signal that something is wrong —
every marking the application actually writes carries one. What denies the
page is the sentinel's unavailable flag, not that spelling (§21.5). And the
same "inventing a fact" argument now reaches the level itself: that spelling
is for the application's own eyes, never disclosed to a reader in a denial
(§21.5), because it would assert a classification nobody made.

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
`UK SECRET` page arrived with no marking at all, and a reader could
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
caller's own principal, so every contributing page individually passed `canView`,
marking gates included; a page the caller cannot see contributes nothing because
it never reached retrieval (§6.7), which is verified by test rather than assumed.
The aggregate exists to tell a human what the text in front of them *is*.

That is structural rather than promised: the type lives in **`RocketWiki.Api`**,
and neither `RocketWiki.Core` (where `MarkingGate` and
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

**Selectors are the union**, listed in configured order, never intersected. A
reader of a compilation needs every codeword any source carried — an answer
drawing on an `APPLE` page and a `BANANA` page is `APPLE BANANA` material — and
the intersection would be the caveat's empty-set trap (next paragraph) in a
different hat: two sources with one selector each, in different categories or
with different values, intersect to *none*, which reads as "no compartment".
Unlike a page (§21.15) an aggregate may therefore carry two values of one
category; the formatter renders them side by side.

**Caveat is a truthful conjunction, and this is the subtle part.** The storage
model holds one eyes-only set per page; an aggregate can have sources with
different ones, and there is no honest single set:

- The **union** — `UK/US EYES ONLY` — says either nationality suffices. That is
  a widening and it is false: the UK source is still UK-only.
- The **intersection** is worse, and it is why this paragraph exists. `{UK} ∩ {US}`
  is **empty**, and an empty eyes-only set in this model means *no caveat at all* —
  so the two most restrictive inputs available would produce the least restrictive
  possible output, silently, while the label looked perfectly correct. That is the
  fail-open trap §21.5 records wearing a different hat: a value that reads as
  "nothing here" when it should read as "everything here".

So distinct source sets are **listed**, comma-separated:
`UK SECRET UK EYES ONLY, US EYES ONLY`, meaning a reader needs both. The comma
is the one separator that survives the loss of brackets (§21.1) without a second
`EYES ONLY` reading as part of the first set. Identical sets collapse to one
entry; a source with no caveat contributes none; the list order is derived from
the sets themselves, so retrieval rank cannot change the rendered bytes. The
invariant that follows is the one to defend: **an empty aggregate caveat means,
and can only mean, "no source had one"** — it is built by filtering for sources
that *have* a caveat, so nothing subtracts and no set-algebra result can reach
empty. Swept by test over every subset of a mixed corpus.

**Prefix carries through only on unanimity** — including unanimous absence — and
any disagreement (`UK` vs none) drops to no prefix, rendering the bare level.
The prefix gates nothing (§21.12), so its only failure mode is
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

**One formatter.** The label is rendered by
`ProtectiveMarking.FormatLabel(prefix, level, selectors, caveatSets, catalog)`,
which `ProtectiveMarking.Format` (a page's `marking.label`) is the one-set
special case of. A client renders `label` verbatim and composes nothing — §21.1's
rule, and the reason the aggregate can never render a caveat or a selector a hair
differently from a page's.

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
from levels, selector values, country sets and prefixes — the things §21.7
confines to the audit table and §21.8 keeps out of every dimension, because a
level or a codeword in a metric tag is a census of the classified estate.
`AssistantTelemetryHygieneTests` plants a sentinel country, a sentinel selector
value and a sentinel prefix, asserts they really do appear in the returned label,
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
  classified content there is a brief window at OFFICIAL, with no selectors,
  between creation and the first `setPageMarking`. The mitigation is procedural
  — create the page empty, mark it, then write — and the alternative (a marking
  on `CreatePageRequest`) would duplicate the vocabulary validation, the
  self-lockout constraint and the audit decision into the create path for a
  window a UI can close.
- **No caveat registry.** One caveat kind, eyes-only, with a fixed country set
  (§21.4). A general caveat registry (types, per-type semantics, per-type
  enforcement) is not built and is not implied by this design.
- **No per-category admin UI.** Selector categories (§21.15) are configuration,
  validated at startup; there is no screen that adds a category or a value,
  on purpose (§6.2). Changing the catalog is a deploy.
- **Selectors do not accumulate down the tree.** Like the level and the caveat
  they are copied once at creation and then owned by the page; a child under an
  `APPLE` parent can be re-marked without `APPLE`, and it is the parent's
  `(protected)` placeholder in the tree (§21.8) — not any propagation — that
  keeps that from being a silent read-around. The child stays reachable by id
  and through search, checked on its own.
- **No "which pages are marked X" report.** The data model supports one without a
  migration — `PageMarkings` is indexed on `Level` and `PageMarkingCountries` on
  `(CountryValue, PageId)`, which is exactly the access path — but the query
  surface does not exist. When it is built it must be permission-filtered **per
  page** the way `GetPagesByLabelAsync` is (§6.4.2): a page the caller cannot view
  is absent entirely, not a redacted row and not implied by a count (§6.7).
- **Markings do not re-mark a subtree.** Changing a parent's marking leaves its
  children exactly as they are; there is no cascade and no bulk re-mark tool.
- **Nationality and group membership are not managed in RocketWiki.** Like
  every other attribute, they live in Keycloak (§6.2, §11.5). RocketWiki
  declares what it reads and reads it. (Clearance and selector eligibility are
  not managed anywhere: neither is an attribute this deployment has, and the
  gates that read them are gone — §21.2.)
- **No declassification schedule, no review dates, no marking expiry.**
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

### 21.15 Additional selectors

A marking may carry **additional selectors**: compartment-style codewords —
`APPLE`, `NORTH` — that narrow a page's audience within its classification.
Each belongs to a **category**, and a page carries **at most one value per
category**.

**Categories are instance configuration, not a registry.** The catalog is
`ProtectiveMarking:SelectorCategories`, a list of
`{ Name, Description, Values[] }` validated at startup
(`docs/CONFIGURATION.md`): names and values canonical upper-case tokens of
`[A-Z0-9_-]`, at most 32 characters, unique; at least one value per category.
A category names **no claim**: it used to carry a `ClaimName` as well, under a
rule that the name could not be one of the well-known keys, and both went with
the eligibility gate below (a stale `ClaimName` in an old environment binds to
nothing and is ignored). Invalid configuration
**fails the host**, in the same family as every other fail-closed setting in
`docs/CONFIGURATION.md`. The configured order is the display order. §6.2 says
why a vocabulary that gates access lives in a diff rather than a table. Core
holds the catalog as a value (`SelectorCatalog`), built once — lazily, from
the bound options at first resolution, after `ValidateOnStart` has already
refused a bad section — and stamped into the data layer's `DbContext` options
through EF's provider-aware `ConfigureDbContext` hook, the way the local
instance id is. Lazily rather than eagerly off the builder, because a test
host layers its configuration in after the top-level statements have run,
and an eager read had every API test gating against an empty catalog while
its own configuration said otherwise; production reads the same values either
way. A context with no catalog holds `SelectorCatalog.Empty`, which knows
nothing and therefore — by the rules below — admits nobody to any
selector-bearing page. Absence fails closed. Clients read the catalog through
`Query.selectorCategories` — name, description and values — which is
unaudited like `classificationScheme` (vocabulary, no subject, no decision)
and empty for an anonymous caller.

**One gate per selector: the grant.** This was two, and the reduction is a
reversal recorded here rather than made quietly.

- **G — grant** is per space and lives in the access grants (§6.4). Every
  selector on the page must be in the union of selector values over the access
  grants the principal matches in that space. G answers "has this space's
  administrator let them", and it is the space's decision alone. The union is
  deliberate: grants add (§6.4), so a person matched by two grants holds both
  audiences' compartments, exactly as they would hold the higher of two roles.
  G consults the catalog for **diagnosis only**: a category this instance has
  not configured — whether it arrived by sync or was removed from the
  configuration — is reported with its own reason, `selector:unknown:`
  (§21.8), rather than as ungranted, so the audit row says "misconfiguration"
  rather than "ask the space admin". It cannot change the verdict: the grant
  writers refuse a value the catalog does not define, so no grant can carry
  one, and an unknown category is granted to nobody either way. There is no
  branch in which an unconfigured category passes.
- **E — eligibility, retired 2026-09-04.** It was site-wide and lived in
  Keycloak: a category could name a `ClaimName`, and the principal's attribute
  of that name had to equal `yes` before the space's grant was consulted. It
  answered "may this person *ever* see this material", and the argument for it
  was that such a decision belongs in the identity provider beside clearance
  and nationality, where RocketWiki reads and never writes. That argument
  assumed an identity provider that states per-category eligibility, and this
  deployment's carries no such attribute — so E, as built, made every category
  that named a claim eligible to nobody, which is not a control but an outage
  with a security-shaped excuse (§21.2). It was removed rather than defaulted
  to eligible: a default `yes` is a gate every principal passes, which is no
  gate, and leaving the code path in place would have been a dormant branch
  nobody could test against a real token. With it went the `ClaimName`, the
  reserved-claim-name rule, the `yes` value and its confined case-folding
  (§6.3), `me.selectorEligibility`, `SelectorCategory.requiresAttribute`, the
  `SELECTOR_ELIGIBILITY` gate and the `selector:not_eligible:` token (§8,
  §21.8). The profile page, which derived eligibility from the mirrored claim,
  shows group membership instead (§6.2).

Whether a person may read `APPLE` material in a space is therefore the space
administrator's decision, made by writing `APPLE` onto an access grant that
matches them, and nothing about the person's account widens or narrows it.
That is a real narrowing of who decides — before, the identity provider held
a veto — and it is accepted with eyes open: an access grant is an audited rule
change (§7), the grant editor offers only catalog values, and a compartment
that must stay closed to someone is a compartment no grant matching them
carries.

Across a marking with several selectors the gate reports every G check in the
marking's canonical category order, so the first failing reason is stable
(§7); a page with no selectors asks no question at all. The truth table over
S, G, N and R — verdict, first-failing token, complete failing set — is pinned
in Core (every one of the 16 view rows, plus the edit rows, plus a sweep
asserting the level changes no row), and in the API as persona × page through
the real mutations, where `pageAccess` and MCP `get_page` must agree on every
cell. It was 64 rows over S, C, E, G, N and R; the live walk-through of
2026-09-03 (README) exercised the E rows and predates their removal.

**Canonical form and storage.** Selector tokens are trimmed and upper-cased on
every path (mutation, import, grant editor), stored sorted by category, and
rendered in configured order; `ProtectiveMarking.Create` refuses two values in
one category at the constructor so the value object cannot exist in an invalid
state, and the database says the same thing with the primary key of
`PageMarkingSelectors`, `(PageId, Category)` — one value per category is a fact
about the table, not application discipline. Grant rows live in
`AccessRuleSelectors`, keyed `(AccessRuleId, Category, Value)` because a grant
may confer several values of one category. Both are child tables, not packed
strings, for the reason `PageMarkingCountry` is (data-model.md). Sync carries
them as an object keyed by category (§21.10); audit writes them in full
(§21.7); a denial reason names the category and never the value, and telemetry
sees neither (§21.8, §15).

**Removing a category from the configuration is safe, and stated.** Pages and
grants keep their rows; every reader fails G on the affected pages with
`selector:unknown:`; `setPageMarking` cannot re-assert the now-unknown value
but can remove it, which is a downgrade and audited as one (§21.6). Nothing
widens, and nothing needs a migration to shrink.

**What the label shows.** Values only, never the category name:
`UK SECRET APPLE NORTH`. The category is bounded configuration vocabulary and
appears in reason tokens, the picker and the inspector; the value is the
codeword, appears in the label because the label is the marking, and appears
nowhere a marking would not (§21.8).

**The development catalog** — `FRUIT` (`APPLE`, `BANANA`) and `REGION`
(`NORTH`, `SOUTH`) — is injected by the AppHost (`DEVELOPING.md`). The dev
realm carries no per-category attribute, so what a dev user lacks for a
FRUIT-marked page is a grant, which any space admin can write and any test can
withhold. (The realm's `fruit` attribute, which fed E, went with it —
`src/RocketWiki.AppHost/keycloak/README.md`.)

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
own error code and message. `clearance` stays on the list even though nobody
holds one any more (§21.12): a refused name teaches the rule once, whereas an
"unknown field" answer would invite exactly the respelling retry the third
property below forbids.

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
   `GetPagesByLabelAsync` use (§6.4.2/§6.7/§9.3). The selector and caveat
   gates arrive free through `EffectivePermissionCalculator.Compute`; §21's gate is not
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
