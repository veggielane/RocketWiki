# RocketWiki — Data Model

Companion to `design.md` §5: the concrete EF Core / SQL Server schema.
Design altitude stays in `design.md`; this file is the table-level truth.

**Status:** Draft — under discussion

---

## Conventions

- **Primary keys:** GUID **v7** (`Guid.CreateVersion7()`), generated
  app-side — time-ordered so clustered indexes don't fragment, and stable
  across the low→high crossing (design.md §12). Exception: append-only,
  instance-local tables (`AuditEvent`, `SyncOutboxEvent`, `PageEmbedding`)
  use `bigint identity` — cheaper, and those rows never cross instances.
- **Timestamps:** `datetime2(3)`, always UTC, suffixed `Utc`.
- **Enums:** stored as `tinyint`, defined in C# with explicit values.
- **Soft delete:** `IsDeleted bit` + `DeletedAtUtc` + `DeletedByUserId` on
  spaces, pages, and attachments (30-day trash). Filtered indexes exclude
  deleted rows; queries filter via a global EF query filter. `Comment` is
  the exception: it carries `IsDeleted` as a tombstone to preserve thread
  shape and therefore gets **no** global query filter.
- **No cascade deletes.** All FKs `ON DELETE NO ACTION` — deletion is an
  explicit, audited operation, never a side effect.
- **Markdown:** `nvarchar(max)` — but reached **by convention** (an unbounded
  `string` with no `HasMaxLength`), never by an explicit
  `HasColumnType("nvarchar(max)")`. The literal type string is SQL Server
  syntax that SQLite cannot parse, so hard-coding it breaks the SQLite test
  tier while producing byte-identical SQL Server DDL. Same for
  `varbinary(max)`.
- EF Core code-first; migrations live in `RocketWiki.Data` and are reviewed
  like code. Provider-specific artifacts (FTS index, vector index,
  partitioning, audit grants) are raw SQL in migrations, skipped on SQLite.

---

## Content

### Space

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| Key | nvarchar(32) | short slug, e.g. `ENG`; unique |
| Name | nvarchar(200) | |
| Description | nvarchar(2000) null | |
| HomepageId | uniqueidentifier null FK → Page | circular FK; nullable, set after first page |
| OriginInstanceId | nvarchar(64) | = local `InstanceId` for native spaces; anything else ⇒ **replica** (read-only) |
| IsExported | bit | low side only: space participates in sync |
| LastOutboxSequence | bigint | per-space outbox counter (see SyncOutboxEvent) |
| IsDeleted / DeletedAtUtc / DeletedByUserId | | soft delete |
| CreatedAtUtc / CreatedByUserId | | |

Indexes: unique `Key` (filtered `IsDeleted = 0`).

### Page

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7; survives sync, so `page://` links work on high |
| SpaceId | uniqueidentifier FK → Space | |
| ParentPageId | uniqueidentifier null FK → Page | null = top level in space |
| AncestorPath | nvarchar(2600) | materialized path of ancestor ids, `/id1/id2/`; see below |
| Slug | nvarchar(200) | from title; unique among live siblings |
| Title | nvarchar(500) | denormalized from current revision |
| SortOrder | int | position among siblings |
| CurrentRevisionNumber | int | optimistic-concurrency anchor (`StaleRevisionError`) |
| CurrentContent | nvarchar(max) | denormalized Markdown of latest revision, FTS-indexed |
| IsDeleted / DeletedAtUtc / DeletedByUserId | | trash |
| DeleteBatchId | uniqueidentifier null | groups the pages trashed by one cascade delete (design.md §6.4.1) |
| CreatedAtUtc / UpdatedAtUtc | | |

Indexes: `(SpaceId, ParentPageId, SortOrder)`; unique
`(SpaceId, ParentPageId, Slug)` filtered `IsDeleted = 0`; `AncestorPath`
(prefix searches); full-text index over `(Title, CurrentContent)`.

**Why `AncestorPath`:** restrictions accumulate down the tree (design.md
§6.4), so every permission check needs the ancestor chain, and the tree UI
needs subtrees. A materialized path gives both in one indexed query
(ancestors by parsing the path, subtree by `LIKE '/id1/id2/%'`) instead of
recursive CTEs on every read. Cost: a page **move rewrites the subtree's
paths** in the move transaction — acceptable because moves are rare and
already heavyweight (visibility warning, audit, outbox event). 2600 chars ≈
70 levels of nesting; the app caps depth well below that.

### PageRevision — immutable

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| PageId | uniqueidentifier FK → Page | |
| RevisionNumber | int | 1-based, dense per page |
| Title | nvarchar(500) | at time of save |
| Content | nvarchar(max) | Markdown |
| EditSummary | nvarchar(500) null | |
| AuthorUserId | uniqueidentifier FK → User | |
| CreatedAtUtc | | |

Indexes: unique `(PageId, RevisionNumber)`. Never updated or deleted
(history retention is an open question in design.md §17).

### Attachment

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7; survives sync (`attachment://` links) |
| PageId | uniqueidentifier FK → Page | |
| FileName | nvarchar(260) | |
| ContentType | nvarchar(127) | |
| SizeBytes | bigint | |
| ContentHash | binary(32) | SHA-256; keys sync-bundle blobs, detects duplicates |
| StorageKey | nvarchar(200) | opaque key into `IFileStorage` (design.md §10) |
| IsDeleted / DeletedAtUtc / DeletedByUserId | | |
| UploadedByUserId / CreatedAtUtc | | |

Indexes: `(PageId)` filtered `IsDeleted = 0`; `(ContentHash)`.

### Comment

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| PageId | uniqueidentifier FK → Page | |
| ParentCommentId | uniqueidentifier null FK → Comment | threading |
| Body | nvarchar(max) | Markdown |
| AuthorUserId | uniqueidentifier FK → User | |
| CreatedAtUtc / EditedAtUtc | | |
| IsDeleted | bit | tombstone — keeps thread shape, body blanked |

Indexes: `(PageId, CreatedAtUtc)`.

### Label / PageLabel

`Label`: Id (PK v7), SpaceId (FK), Name nvarchar(100); unique
`(SpaceId, Name)`. `PageLabel`: composite PK `(PageId, LabelId)`.

---

## Identity & access

### User

Local mirror for display only — **authorization never reads this table**
(design.md §6.1).

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| Subject | nvarchar(255) null | OIDC `sub`; **null for shadow users** from sync |
| Email | nvarchar(320) null | |
| DisplayName | nvarchar(200) | |
| AttributesJson | nvarchar(max) | mirrored registered attributes; admin-visible only |
| IsExternal | bit | shadow user (sync author), never loginable |
| CreatedAtUtc / LastSeenAtUtc | | |

Indexes: unique `Subject` filtered `Subject IS NOT NULL`; `(Email)`.

### AccessRule

One table, two kinds (design.md §6.4), with real FKs instead of a generic
subject id so referential integrity holds:

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| Kind | tinyint | 1 = SpaceGrant, 2 = PageRestriction |
| SpaceId | uniqueidentifier null FK → Space | set iff Kind = SpaceGrant |
| PageId | uniqueidentifier null FK → Page | set iff Kind = PageRestriction |
| Role | tinyint null | grants: 1 viewer, 2 editor, 3 space-admin |
| Action | tinyint null | restrictions: 1 view, 2 edit |
| ExpressionJson | nvarchar(max) | validated AND/OR expression tree (§6.3) |
| CreatedAtUtc / CreatedByUserId / UpdatedAtUtc / UpdatedByUserId | | changes also emit `permission.change` audit events |

Check constraints enforce the kind ↔ column pairing. Indexes: `(SpaceId)`,
`(PageId)`. The full rule set is small and cached in memory; these tables
are read on startup and cache invalidation, not per request.

### AttributeDefinition — the attribute registry (design.md §6.2)

Id (PK v7), Key nvarchar(64) unique, ClaimName nvarchar(128), DisplayName
nvarchar(128), Type tinyint (1 string, 2 string[]), AllowedValuesJson
nvarchar(max) null.

### KnownGroup — rule-builder picker source

Id (PK v7), Name nvarchar(255) unique, Source tinyint (1 observed at login,
2 manual), FirstSeenAtUtc.

---

## Notifications (design.md §8)

### Watch — explicit subscriptions

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| UserId | uniqueidentifier FK → User | |
| SpaceId | uniqueidentifier null FK → Space | watch a whole space |
| PageId | uniqueidentifier null FK → Page | …or a single page (incl. its subtree) |
| CreatedAtUtc | | |

Exactly one of `SpaceId` / `PageId` is set (check constraint
`CK_Watches_SpaceXorPage`). Uniqueness is **two filtered indexes**, not one
composite:

```
UNIQUE (UserId, SpaceId) WHERE SpaceId IS NOT NULL
UNIQUE (UserId, PageId)  WHERE PageId  IS NOT NULL
```

A single `UNIQUE (UserId, SpaceId, PageId)` looks right but protects
nothing: EF Core auto-filters a unique index over nullable columns to
require *all* of them non-null, and the xor constraint guarantees one is
always null — so the filter matches zero rows, forever. The two filtered
indexes express what is actually meant: a user cannot watch the same space,
or the same page, twice.

Index `(PageId)` and `(SpaceId)` for recipient lookup at send time.

### Notification — persisted, so offline users catch up

| Column | Type | Notes |
|---|---|---|
| Id | bigint identity PK | instance-local, never synced |
| RecipientUserId | uniqueidentifier FK → User | |
| Type | tinyint | page updated / comment reply / mention / sync imported |
| PageId | uniqueidentifier null FK → Page | |
| SpaceId | uniqueidentifier null FK → Space | |
| ActorUserId | uniqueidentifier null FK → User | null for system events |
| TitleSnapshot | nvarchar(500) null | page title **as permitted at send time** |
| CreatedAtUtc | | |
| ReadAtUtc | datetime2(3) null | |

Index `(RecipientUserId, CreatedAtUtc)` filtered `ReadAtUtc IS NULL` for the
unread badge; `(RecipientUserId, CreatedAtUtc)` for the full list.

**Presence has no table.** Who is viewing a page, their pointer position,
and (later) their caret are ephemeral hub state, held in memory and gone
when the connection drops. Nothing about live presence is persisted or
audited (design.md §8).

**Rows exist only for recipients who passed `canView` at send time** — or,
for recipients with no live token at write time (sync imports, offline
fan-out recipients), a row with `TitleSnapshot NULL` whose existence is
gated at read. Snapshot rows are created *after* the permission check, not
filtered on read. That keeps the leak-prevention in one place (design.md
§8). Re-check `canView` when rendering the list anyway: a snapshot row
written last week may name a page the user can no longer see, and stale
titles must not resurface; a null-snapshot row that fails the read-time
check is withheld entirely.

---

## Audit

### AuditEvent — append-only (design.md §7)

| Column | Type | Notes |
|---|---|---|
| Id | bigint identity | |
| TimestampUtc | datetime2(3) | partition key |
| UserId | uniqueidentifier null FK → User | null for system actors (e.g. sync CLI) |
| Action | varchar(64) | `page.view`, `sync.import`, … |
| SubjectType | tinyint null | page / space / attachment / comment / rule |
| SubjectId | uniqueidentifier null | |
| SpaceKey | nvarchar(32) null | denormalized for cheap filtering |
| Outcome | tinyint | 1 success, 2 denied |
| Channel | tinyint | 1 graphql, 2 mcp, 3 attachment, 4 sync, 5 system |
| RequestId | varchar(64) | |
| ClientIp | varchar(45) | IPv6-safe |
| McpClient | nvarchar(128) null | |
| DetailsJson | nvarchar(max) null | per-action payload (rule before/after, query text, failing restriction) |

Clustered PK `(TimestampUtc, Id)` aligned to **monthly partitions** on
`TimestampUtc` so archival is partition switch-out, not row deletes.
Secondary indexes: `(UserId, TimestampUtc)`, `(SubjectId, TimestampUtc)`,
`(Action, TimestampUtc)`. The app's SQL login has INSERT/SELECT only on
this table — append-only is enforced by the database, not convention.

---

## Sync (design.md §12)

### SyncOutboxEvent — low side, append-only

| Column | Type | Notes |
|---|---|---|
| Id | bigint identity | |
| SpaceId | uniqueidentifier FK → Space | exported spaces only |
| SequenceNumber | bigint | **gap-free per space** — see below |
| EventType | tinyint | page upsert / move / delete / restore, comment, attachment, labels, restrictions |
| PayloadJson | nvarchar(max) | full Markdown, not diffs; attachments by ContentHash. PageUpsert payloads carry no revision data in the journal; the export job attaches the page's revision history (bundle format 2) by joining `(pageId, revisionNumber)` back to PageRevisions at drain time |
| CreatedAtUtc | | |
| ExportedInBundle | int null | stamped by the export job |

Unique `(SpaceId, SequenceNumber)`.

**Why not identity for the sequence:** identity columns have gaps (rollbacks
burn values), and import-side gap detection (design.md §12) requires a
gap-free sequence. So `Space.LastOutboxSequence` is incremented inside the
same mutation transaction that writes the event — contention is per-space
and acceptable.

### SyncImportState / SyncSpaceState — high side

`SyncImportState`: OriginInstanceId nvarchar(64) PK, LastBundleNumber int,
LastManifestHash char(64), LastImportAtUtc. One row per origin (supports
multiple lows if that's decided).

`SyncSpaceState`: PK `(OriginInstanceId, SpaceId)`, AppliedSequence bigint —
the per-space high-water mark that makes import idempotent and gap-refusing.

---

## AI (design.md §9)

### PageEmbedding — derived, never synced, rebuilt locally

| Column | Type | Notes |
|---|---|---|
| Id | bigint identity PK | |
| PageId | uniqueidentifier FK → Page | |
| ChunkIndex | int | |
| HeadingPath | nvarchar(1000) | for section deep-links |
| ChunkHash | binary(32) | skip unchanged chunks on re-embed |
| Embedding | vector(1536) | SQL Server 2025 native type — **dimensions are fixed per column**; changing the model is a migration + full re-embed. *Currently mapped to `varbinary(max)` via a value converter; the native type and DiskANN index land with milestone 7 (design.md §9).* |
| Model | nvarchar(128) | sanity check against config at query time |
| UpdatedAtUtc | | |

Unique `(PageId, ChunkIndex)`; DiskANN vector index on `Embedding`
(cosine). On SQLite (tests) this table maps `Embedding` to a blob and the
in-memory cosine fallback handles search.

### GitLabCredential — per-user GitLab PAT, encrypted, instance-local

| Column | Type | Notes |
|---|---|---|
| UserId | uniqueidentifier PK, FK → User | PK = FK (the PageEmbeddingState pattern): one credential per user |
| ProtectedToken | nvarchar(1024) | ASP.NET Data Protection output, never plaintext; never readable via any API |
| CreatedAtUtc / UpdatedAtUtc | datetime2(3) | |

Never synced, never exported (no `SyncEventType`; the outbox classifier has
no case for its events). `ON DELETE NO ACTION` like every FK.

### UserAvatar — per-user profile picture, instance-local

| Column | Type | Notes |
|---|---|---|
| UserId | uniqueidentifier PK, FK → User | PK = FK (the PageEmbeddingState pattern): one avatar per user |
| StorageKey | nvarchar(200) | opaque key into `IFileStorage` (`avatars/{yyyy}/{MM}/{guid}`); replaced on re-upload, old object left for the §10 janitor |
| SizeBytes | bigint | of the canonical stored PNG |
| ContentHash | binary(32) | SHA-256 of the stored bytes; the ETag on both GET routes |
| EmailHashMd5 | varchar(32) null | lowercase hex MD5 of the trim+lowercase mirrored email; null when no email |
| EmailHashSha256 | varchar(64) null | lowercase hex SHA-256, same normalization; both stored because Gravatar consumers disagree (historic MD5, current-spec SHA-256, Libravatar both) |
| CreatedAtUtc / UpdatedAtUtc | datetime2(3) | UpdatedAtUtc tracks the image, not hash refreshes |

Indexes: `(EmailHashMd5)` and `(EmailHashSha256)`, filtered `IS NOT NULL`,
deliberately **non-unique** — `User.Email` is not unique (shadow users can
mirror a real address), so the anonymous hash lookup resolves ties
deterministically instead of the schema forbidding them. Content is always
the server-re-encoded 512×512 PNG (no ContentType column to disagree with
that construction). Never synced, never exported; the hashes are re-derived
by JIT provisioning whenever the mirrored email changes. `ON DELETE NO
ACTION` like every FK.

### CustomEmoji — admin-curated `:name:` registry, instance-local

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK (v7) | |
| Name | nvarchar(64) unique | grammar `[a-z0-9_-]{1,64}`; lowercase-only makes uniqueness case-insensitive by construction, no collation dependence |
| ContentType | nvarchar(127) | `image/png` or `image/gif` (animated) |
| SizeBytes | bigint | of the re-encoded stored image |
| ContentHash | binary(32) | source of the serve-route ETag |
| StorageKey | nvarchar(200) | `emojis/{guid}` in IFileStorage |
| PixelSize | int | stored images are always square, 32–256 |
| CreatedByUserId | FK → User | NO ACTION |
| CreatedAtUtc | datetime2(3) | |

Hard delete, no query filter (registry vocabulary, not user content).
Never synced. The audit channel `attachment` denotes the binary-HTTP
surface (`/attachments`, `/avatars`, `/emojis`), not the Attachment
subject.

### PageEmbeddingState — job bookkeeping, instance-local

One row per page: `PageId` (PK = FK), `EmbeddedRevisionNumber int`,
`FailedAttempts int`, `LastAttemptAtUtc`, `UpdatedAtUtc`. The embedding
job's trigger is a scan — a page is due when this row is missing or its
`EmbeddedRevisionNumber` differs from `Page.CurrentRevisionNumber` — chosen
over event wiring because it also catches the sync CLI's out-of-process
imports (design.md §9.4) and survives restarts. Purged with the chunk rows
when a page is trashed; the absence re-embeds on restore. Like
`PageEmbedding`: derived, never synced, no audit rows (system action).

---

## Temporal tables — considered and rejected

SQL Server system-versioned temporal tables were evaluated for `AccessRule`,
`Space`, and `Page`, and **deliberately rejected**. Recorded here because the
idea is a good one and will be proposed again.

**Reason: SQLite has no equivalent.** Provider-conditional `IsTemporal()`
would leave the versioning behaviour testable only in the SQL Server
Testcontainers tier, while the SQLite tier — the bulk of the suite
(design.md §14) — would exercise a materially different schema. That trade
was refused: keeping one schema that every tier can exercise is worth more
than the feature. The SQLite tier has already justified itself by catching
two portability bugs (`nvarchar(max)`, composite-key identity) that
hand-reading a migration missed; widening the gap between test and
production schemas would erode exactly that value.

### What this costs, and how it is covered

Temporal versioning would have made "what were the rules in April?"
answerable from state, independent of application correctness. Without it,
**`AuditEvent` is the only record of rule history**, which makes it
load-bearing rather than merely useful. Therefore:

- An `AccessRule` change **must record complete before-and-after rule state**
  in `AuditEvent.DetailsJson` — full expression JSON on both sides, never a
  diff or a summary — so the rule set at any past instant is reconstructable
  by replaying audit events. This is a hard requirement with a test, not a
  convention.
- The same applies to grant/restriction creation and deletion: the absent
  side is recorded as explicitly null, so a replay can tell "created" from
  "unchanged".
- Page metadata history (title, parent, position) is already covered by
  `PageRevision` for content and by `page.move` audit events for structure.

Note this was never a question of replacing `AuditEvent`: audit records *who
did what*, temporal records *what a row looked like*, and a history row has
no actor. Temporal would have been corroboration, and corroboration is what
we have given up.

---

## Decisions taken here (flag if you disagree)

1. **GUID v7 PKs** for anything that can cross instances; `bigint identity`
   for instance-local journals.
2. **Materialized `AncestorPath`** over recursive CTEs or `hierarchyid` —
   readable, portable to SQLite tests, and move-cost lands on the rare
   operation instead of every read.
3. **Gap-free per-space outbox sequence** via a counter column on `Space`,
   because identity gaps would false-alarm the import gap detector.
4. **`AccessRule` as one table with paired nullable FKs + check
   constraints**, keeping real referential integrity for both kinds.
5. **Comment tombstones** (`IsDeleted`, body blanked) so threads keep shape.
6. **Audit clustered on `(TimestampUtc, Id)` with monthly partitions** so
   retention is partition switch-out.
7. **Notifications are materialized per recipient after a permission check**,
   rather than stored once and filtered on read — the permission decision
   happens in exactly one place, and re-checked again at render.

## Open items

- [ ] Embedding dimension is written as 1536 — confirm once the model per
      instance is chosen (design.md §17).
- [ ] `DetailsJson` schema per audit action — define alongside each feature.
- [ ] Whether `Space.Key` is immutable after creation (recommended: yes;
      renames touch URLs, sync identity, and audit `SpaceKey`).
