# RocketWiki — Data Model

Companion to `design.md` §5: the concrete EF Core / SQL Server schema.
Design altitude stays in `design.md`; this file is the table-level truth.

**Status:** Draft — under discussion

---

## Conventions

- **Primary keys:** GUID **v7** (`Guid.CreateVersion7()`), generated
  app-side — time-ordered so clustered indexes don't fragment, and stable
  across the low→high crossing (design.md §12). Exception: append-only,
  instance-local tables (`AuditEvent`, `SyncOutboxEvent`, `PageEmbedding`,
  `Notification`) use `bigint identity` — cheaper, and those rows never
  cross instances.
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
| OwnerUserId | uniqueidentifier | designated **accountable person** (design.md §6.5) — governance metadata that **confers no access**: the rule engine never reads it, and an owner who should administer the space needs a space-admin grant like anyone else. Distinct from `CreatedByUserId`, which is immutable history. **No FK**, like `CreatedByUserId`: sync materializes replica spaces with `Guid.Empty` (users don't cross the boundary, §12), so an FK would make import impossible — hence `Space.owner` is nullable in GraphQL. Reassigned via `setSpaceOwner` (canManageAccess, §6.5.2), which is allowed on a replica because ownership is local curation |

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
| Icon | nvarchar(32) null | optional decoration (`PageIcon`); null is "no icon", a real state. Stored by NAME, never by number — an integer column would make inserting an enum member repaint every existing page. An unrecognised name reads back as null |
| SortOrder | int | position among siblings |
| CurrentRevisionNumber | int | optimistic-concurrency anchor (`StaleRevisionError`) |
| CurrentContent | nvarchar(max) | denormalized Markdown of latest revision, FTS-indexed |
| IsDeleted / DeletedAtUtc / DeletedByUserId | | trash |
| DeleteBatchId | uniqueidentifier null | groups the pages trashed by one cascade delete (design.md §6.4.1) |
| CreatedAtUtc / UpdatedAtUtc | | |

Indexes: `(SpaceId, ParentPageId, SortOrder)`; unique
`(SpaceId, ParentPageId, Slug)` filtered `IsDeleted = 0`; `AncestorPath`
(prefix searches); `DeleteBatchId` filtered `DeleteBatchId IS NOT NULL`
(`IX_Pages_DeleteBatchId` — restore looks up every page sharing a cascade
delete's batch id, and only trashed pages ever have one, so the filtered
index stays small); full-text index over `(Title, CurrentContent)`.

Every page also has exactly one `PageMarking` row (design.md §21) — its
protective marking, which gates `canView`. Not a column here, because the
eyes-only caveat is a set and needs its own child table; and keeping it off
`Page` means the marking cannot ride along on a projection that skipped
authorization.

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

### PagePropertyKey — the admin-defined key registry (design.md §20)

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK (v7) | |
| Key | nvarchar(64) | display form, trimmed — `Owner`, `Review Date` |
| KeyNormalized | nvarchar(64) | `Trim().ToLowerInvariant()`; **this** is the unique one |
| Description | nvarchar(256) null | admin hint on the properties screen |
| SortOrder | int | display order; ties break on `Key` |
| CreatedByUserId | uniqueidentifier null FK → User | **null** for a key materialized by sync import — no local actor |
| CreatedAtUtc | datetime2(3) | |

Indexes: unique `KeyNormalized`; `(SortOrder, Key)` for the listing.

GUID v7 rather than `bigint identity` because the table is not an append-only
journal — but note the id deliberately does **not** cross instances: sync
payloads carry the key's *name* and the import side finds-or-creates by
`KeyNormalized` (design.md §20.4), so two instances hold different ids for
the same key and neither is wrong.

**Uniqueness is on `KeyNormalized`, not `Key`, and that is load-bearing.**
SQL Server's default collation is case-insensitive while SQLite's is
case-sensitive for ASCII, so a unique index on the raw key would enforce a
*different rule per provider* — `Owner`/`owner` colliding in production and
coexisting in the SQLite tier. Normalizing in the application makes the rule
identical on both. (Contrast `CustomEmoji.Name`, which gets the same
guarantee free because its grammar admits lowercase only, and
`SqlServerFileStorage`'s BIN2 key column, which solves the same class of
problem in the opposite direction.)

**The other five string lookup keys take the BIN2 route instead**, in the
`BinaryCollationOnStringKeys` migration: `Space.Key`, `Page.Slug`,
`Label.Name`, `KnownGroup.Name` and `AttributeDefinition.Key` are all declared
`COLLATE Latin1_General_100_BIN2` on SQL Server (applied in
`RocketWikiDbContext.OnModelCreating`'s provider branch, like
`PageEntry.Collection`, because the collation *name* is SQL Server's and SQLite
has never heard of it). Each is both a unique index and a lookup predicate
translated to SQL, so its case sensitivity used to be the provider's rather
than the application's: `ENG` and `eng` were two spaces on SQLite and one on
SQL Server, and `/spaces/eng/x` resolved a page in production that the test
tier 404s — two tiers enforcing different rules, with the looser one running on
every commit.

Binary rather than a normalized column, deliberately. §6.3 already makes rule
matching exact and ordinal ("guessing at what an admin meant is exactly the
wrong instinct"), `PageQueryService` already documents ordinal space keys as
the correct reading, and SQLite is case-sensitive here already — so this makes
production agree with the behaviour the whole suite already pins, rather than
inventing a third. `KnownGroup.Name` and `AttributeDefinition.Key` are the
sharpest of the five: the rule engine compares group names and attribute keys
**ordinally in memory** (`Principal.Create`), so a case-folded registry could
offer the rule builder an `engineering` that no token spelling ever matches —
§21.4's fails-closed-while-looking-correct trap, one layer up.

**On an existing database this migration cannot fail and cannot invalidate a
row.** CI → BIN2 only ever *relaxes* uniqueness, and the unique index already
prevented a colliding pair from existing, so there is nothing to reconcile.

### Canonical forms, and why URLs are still case-insensitive

A case-sensitive index would ordinarily make `/spaces/eng/my-page` a 404 next
to `/spaces/ENG/My-Page`. It does not, because the two halves of a page address
are stored in **canonical form** and every lookup **normalizes its input**
before comparing:

| | Canonical form | Applied by |
|---|---|---|
| `Space.Key` | trimmed, UPPER (`ENG`) | `SpaceKeys.Canonical` |
| `Page.Slug` | trimmed, lower (`my-page`) | `PageSlugs.Canonical` |

Both halves are required and neither works alone. Canonical storage without
normalized lookups 404s the casing the user typed. Normalized lookups without
canonical storage are *ambiguous*: `my-page` and `My-Page` could both exist —
BIN2 sees them as different — and one URL would then address two pages with no
defined winner. Together they also give something a case-insensitive collation
could not: a case-sensitive unique index over uniformly-cased values enforces
**case-insensitive uniqueness**, so creating `My-Page` beside `my-page` is
refused as taken.

Storage is canonicalized at the **persistence seam**
(`RocketWikiDbContext.EnsureCanonicalAddresses`, the shape `EnsurePageMarkings`
established) so no write path can lose it — including the sync importer, which
may apply a bundle written by an instance older than this rule. The mutation
services canonicalize the caller's input *as well*, because their "is this
taken" pre-check has to run against the canonical value: a pre-check on the raw
value would pass and then hit the unique index at commit, turning a clean
`ValidationError` into a 500.

Lookups normalize **in the services** rather than at the resolver edge, and the
choice is deliberate: a new entry point — another resolver, an MCP tool, a
future REST route — reaches a page or space by name only through
`PageReadService.FindPageIdBySlugAsync`, `SpaceReads`, `SearchService`,
`AnalyticsService` or the label listings, so it inherits the rule; an
edge-level helper is exactly the thing a new entry point forgets.

The `CanonicalizeSpaceKeysAndPageSlugs` migration folds existing rows. It
**fails loudly** rather than corrupting if two live rows would collide, which
only a database that spent time between the two migrations can hold.

Two consequences worth stating. **Slug generation was already lowercase on both
sides** — the SPA's `slugifyTitle` (`web/src/pages/pageSlug.ts`) and the
Confluence importer's `Slugifier` — so this changes nothing about slugs the
product *derives*; what it fixes is the hand-edited slug the create dialog
allows. And **`AuditEvent.SpaceKey` is deliberately not migrated**: the audit
table is append-only (design.md §7), and rewriting historical rows to tidy a
report is the wrong instinct. Rows written before this land carry whatever
casing they were given.

**Labels are deliberately not part of this.** A space key is an address; a label
is a user-chosen *name*, offered by a picker and displayed as written. Folding
it would need either a normalized column beside the display name (the
`PagePropertyKey.KeyNormalized` shape, and a real product decision about whether
`Design` and `design` are one label) or a `LOWER()` comparison that gives up the
index — neither of which follows from "URLs are case-insensitive". Label
matching is therefore ordinal on both providers now, which is *consistent* where
it used to differ per tier. The visible consequence: a space can hold both
`Design` and `design` as separate labels on SQL Server, as it always could on
SQLite.

Hard delete, no query filter — and a key in use cannot be deleted at all
(the service refuses, naming the usage count), so there is nothing for a
tombstone to protect. Never synced as a table.

### PageProperty — the values (design.md §20)

| Column | Type | Notes |
|---|---|---|
| PageId | uniqueidentifier FK → Page | part of PK |
| PagePropertyKeyId | uniqueidentifier FK → PagePropertyKey | part of PK |
| Value | nvarchar(1000) | plain text, never empty — clearing removes the row |
| UpdatedAtUtc | datetime2(3) | |
| UpdatedByUserId | uniqueidentifier null FK → User | **null** for a row applied by sync import |

Composite PK `(PageId, PagePropertyKeyId)` — one value per key per page, so
"set" is an upsert and the row needs no surrogate id of its own, exactly like
`PageLabel`. Indexes: the PK, plus `(PagePropertyKeyId, PageId)` — the "which
pages use this key" path, used today by the delete-key-in-use check and the
access path a future space-level property report would need, which is why the
table is shaped this way now rather than after a migration.

All FKs `ON DELETE NO ACTION` like everything else. **No global query
filter**, matching `PageLabel`, with the same consequence: rows for a
soft-deleted page linger, so any cross-page query over this table must join
to `Pages` rather than assume every row belongs to a live page.

### PageMarking — the protective marking (design.md §21)

| Column | Type | Notes |
|---|---|---|
| PageId | uniqueidentifier PK, FK → Page | **the PK is the page id** — 1:1 by construction |
| Level | tinyint | `ClassificationLevel`: 1 OFFICIAL, 2 OFFICIAL_SENSITIVE, 3 SECRET, 4 TOP_SECRET |
| Prefix | nvarchar(16) null | national qualifier — **only `UK` or NULL is writable** (the mutation takes `ukPrefix: Boolean!`; the API exposes `ukPrefix`, not the string). `UK` by default, giving `UK SECRET`; **NULL is legal** and means no prefix (design.md §21.12). The column stays a string rather than a bit so a sync-imported legacy value renders verbatim; `AddMarkingSelectorsAndFixedCaveat` nulled every stored value other than `UK` — on `PageMarkings` and on `PageEntries` alike |
| SetAtUtc | datetime2(3) | |
| SetByUserId | uniqueidentifier null FK → User | **null** for a row applied by sync import, or by the every-page-is-marked backstop — no local actor |

Indexes: the PK, plus `Level` — "which pages sit at or above X" is the
administrative sweep the OFFICIAL backfill makes necessary (§21.11), and every
*enforcement* read is a PK lookup, so the table needs nothing else. **`Prefix` is
deliberately unindexed**: it gates nothing, so no enforcement or filtering path
ever looks a page up by it.

`Prefix` is nullable rather than defaulted-not-null because "no prefix" is a real
marking that must stay clearable — a NOT NULL column would have forced a sentinel
value. It is set by its own migration (`AddPageMarkingPrefix`) rather than by
editing `AddPageMarkings`, which is already applied: editing an applied migration
produces a schema that cannot be reproduced from zero.

**PK = PageId is the whole enforcement of "one marking per page".** A second
marking for a page is a primary-key violation rather than something application
code has to prevent — for a table that gates access, "which marking applies" must
not be a question with two possible answers.

`Level` is a **tinyint, not a string**: the ordering *is* the access comparison
(§21.1), so it must be numeric on every provider, and a value outside the
four-member ladder cannot be typed in. The wire formats (GraphQL enum, sync
payload, audit details) all use the member's name; only storage is numeric.

**No global query filter**, matching `PageProperty` and `PageLabel`: a
soft-deleted page keeps its marking, which is what makes restore give the page
back with the classification it had. Every page has exactly one row — creation
writes it, sync import writes it, `RocketWikiDbContext` materializes one for any
page inserted without it, and the `AddPageMarkings` migration backfilled every
page that predated the feature. A page found *without* one is read as TOP SECRET
(§21.5); that is a diagnosis, never a mode.

**Aggregate markings (§21.13) add no storage, and that is the point.** The label a
search result list or an Ask answer carries is computed per response from the
markings of the pages that fed it, and thrown away with the response. There is no
aggregate column, no aggregate table, and no page whose stored marking was derived
from an aggregate: a compilation's label is a fact about one response, not a
classification anybody decided. The only place one is ever written down is the
`assistant.ask` audit row's `DetailsJson`, as the rendered label — the same
"a mutable row's history lives only in the audit log" reasoning as §21.7.

### PageMarkingCountry — the eyes-only set (design.md §21.4)

| Column | Type | Notes |
|---|---|---|
| PageId | uniqueidentifier FK → PageMarking | part of PK |
| CountryValue | nvarchar(32) | canonical: trimmed, `ToUpperInvariant()` |

Composite PK `(PageId, CountryValue)` — a country appears at most once per page,
so applying a set is idempotent and there is no ordering question between two
rows for the same country, exactly like `PageLabel`. Indexes: the PK, plus
`(CountryValue, PageId)` — the "which pages are releasable to X" access path,
which has no query surface yet (§21.14) but is the reason the table is shaped
this way now rather than after a migration.

A **normalized child table, not a delimited column on `PageMarking`**, and that
is deliberate for enforcement-critical data: `LIKE '%GB%'` over a packed string
is exactly the kind of near-miss that quietly answers the wrong question about an
access control.

Canonical order for display and serialization is **derived** (ordinal sort), not
stored — a sort column would be one more thing that can disagree. Comparison
happens in memory, never in SQL, for the same tier-parity reason
`PagePropertyKey.KeyNormalized` exists: SQL Server's default collation is
case-insensitive and SQLite's is case-sensitive for ASCII, so a provider-side
comparison would enforce two different rules across the two test tiers.
Canonicalizing on write makes the answer byte-identical everywhere.

Values are the **fixed national-caveat vocabulary** `AUS`, `CAN`, `NZ`, `UK`,
`US` (`NationalCaveatVocabulary`, design.md §21.4) — no longer the registered
`nationality` attribute's `AllowedValuesJson`, and never an ISO 3166 list. The
column is deliberately **not** check-constrained to the five: a legacy token
(a pre-fixed-set `GB` was remapped to `UK` by `AddMarkingSelectorsAndFixedCaveat`,
the same nation; anything else is left in place, with a review query in the
migration's comment) stays on the row and matches nobody, which is the
fail-closed reading, while the mutation refuses to write a new one.

### PageMarkingSelector — the additional selectors (design.md §21.15)

| Column | Type | Notes |
|---|---|---|
| PageId | uniqueidentifier FK → PageMarking | part of PK |
| Category | nvarchar(32) | canonical: trimmed, `ToUpperInvariant()`; part of PK |
| Value | nvarchar(32) | canonical: trimmed, `ToUpperInvariant()` |

Composite PK **`(PageId, Category)`** — one value per category per page is a
fact about the table, not application discipline: a second value for a
category is a primary-key violation, which is the same construction
`PageMarking`'s PK uses for "one marking per page". `ProtectiveMarking.Create`
refuses the same thing at the value object. Indexes: the PK, plus
`(Category, Value, PageId)` — the "which pages carry APPLE" access path, shaped
now for the reason `PageMarkingCountry`'s is. FK `NO ACTION` like everything
else; no global query filter, like the country table, so a soft-deleted page
keeps its selectors through restore.

Values are validated against the configured selector catalog on every write
path (mutation, grant editor, and length-checked on import), because SQLite does
not enforce `HasMaxLength` and a hostile bundle must land as an unparseable
marking rather than a `DbUpdateException`. An imported category or value the
instance has not configured is stored verbatim and matches nobody (design.md
§21.10). Added by `AddMarkingSelectorsAndFixedCaveat`, together with the prefix
nulling and the `GB` → `UK` remap above.

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

One table, three kinds (design.md §6.4), with real FKs instead of a generic
subject id so referential integrity holds:

| Column | Type | Notes |
|---|---|---|
| Id | uniqueidentifier PK | v7 |
| Kind | tinyint | 1 = RoleGrant, 2 = PageRestriction, 3 = AccessGrant |
| SpaceId | uniqueidentifier null FK → Space | set iff Kind ∈ {1, 3} |
| PageId | uniqueidentifier null FK → Page | set iff Kind = PageRestriction |
| Role | tinyint null | role grants only: 2 editor, 3 space-admin. **1 (viewer) is retired** — a viewer is an access grant now, and the check constraint forbids the value |
| Action | tinyint null | restrictions: 1 view, 2 edit |
| ExpressionJson | nvarchar(max) | validated AND/OR expression tree (§6.3) |
| CreatedAtUtc / CreatedByUserId / UpdatedAtUtc / UpdatedByUserId | | changes also emit `permission.change` audit events |

`CK_AccessRules_KindColumnPairing` enforces the kind ↔ column pairing:

```
([Kind] = 1 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL AND [Role] IS NOT NULL AND [Role] IN (2,3) AND [Action] IS NULL)
OR ([Kind] = 2 AND [PageId] IS NOT NULL AND [SpaceId] IS NULL AND [Action] IS NOT NULL AND [Role] IS NULL)
OR ([Kind] = 3 AND [SpaceId] IS NOT NULL AND [PageId] IS NULL AND [Role] IS NULL AND [Action] IS NULL)
```

The explicit `[Role] IS NOT NULL` is load-bearing, not redundant: `NULL IN (2,3)`
evaluates to UNKNOWN, and a CHECK constraint accepts UNKNOWN, so without it a
kind-1 row with no role at all would pass the constraint. The SQLite tier caught
exactly that when the constraint was first written with `IN (2,3)` alone.

SQLite enforces CHECK constraints, so the pairing is covered by the SQLite tier.
Indexes: `(SpaceId)`, `(PageId)`. The full rule set is small and cached in
memory; these tables are read on startup and cache invalidation, not per request.

**Access and role grants are two kinds in one table**, not two tables: one grants
query loads both for a space, one check constraint pairs their columns, and one
`permission.change` snapshot shape audits both. The `SplitSpaceGrantsIntoAccessAndRole`
migration made the split behaviour-preserving: the old check constraint is
dropped, every kind-1 `Role = 1` (viewer) row becomes `Kind = 3, Role = NULL` in
place, a **mirror** kind-3 row with the same expression and audit columns is
inserted beside every kind-1 editor and space-admin row (so every editor keeps
exactly the visibility they had), and the new constraint is added last. The
mirror rows carry no `permission.change` audit row — design.md §7 records the
discontinuity and why a migration does not write audit rows.

### AccessRuleSelector — selector values an access grant confers (design.md §21.15)

| Column | Type | Notes |
|---|---|---|
| AccessRuleId | uniqueidentifier FK → AccessRule | part of PK |
| Category | nvarchar(32) | canonical; part of PK |
| Value | nvarchar(32) | canonical; part of PK |

Composite PK **`(AccessRuleId, Category, Value)`** — unlike `PageMarkingSelector`,
a grant may confer **several values of one category** (the readers it matches
hold all of them), so the value is part of the key. Index
`(Category, Value, AccessRuleId)` — "which grants confer APPLE". FK `NO ACTION`;
selector rows are removed explicitly when a rule is deleted. Only kind-3 rows
carry them, enforced by the service (a role grant or restriction submitted with
selector values is a `ValidationError`), and every value is validated against
the configured catalog on write. Added by `SplitSpaceGrantsIntoAccessAndRole`.

### AttributeDefinition — the attribute registry (design.md §6.2)

Id (PK v7), Key nvarchar(64) unique, ClaimName nvarchar(128), DisplayName
nvarchar(128), Type tinyint (1 string, 2 string[]), AllowedValuesJson
nvarchar(max) null.

Two keys are **well known** to code as well as to admins: `nationality`, which
gates the eyes-only caveat (`PageMarkingCountry`, design.md §21.4) — but whose
`AllowedValuesJson` is **no longer read by anything in markings**: the caveat's
vocabulary is the fixed five-token set, and a `nationality` row, where one
exists, is ordinary rule-builder vocabulary for `attr` conditions — and
`clearance`, whose values are the four `ClassificationLevel` wire names and
which gates every page read against its protective marking (§21.3). Both are
still ordinary registered attributes — read from the token per request like any
other — and both are absent rather than empty when the claim is missing, which
is what makes their fail-closed defaults land on the intended answer.

The **selector categories** (design.md §21.15) are deliberately *not* registry
rows. They come from configuration (`ProtectiveMarking:SelectorCategories`), the
claims they name are mapped into the `Principal` from that catalog rather than
from this table, and JIT provisioning never mirrors those claims into
`User.AttributesJson` — eligibility is an access input, not display data, and a
vocabulary that gates access lives in a reviewed diff, not in an editable row
(design.md §6.2).

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

Clustered PK `(TimestampUtc, Id)`, designed to align to **monthly
partitions** on `TimestampUtc` so archival is partition switch-out, not row
deletes. Secondary indexes: `(UserId, TimestampUtc)`,
`(SubjectId, TimestampUtc)`, `(Action, TimestampUtc)`.

**Status: the partition function/scheme and the app-login grants are
designed, not yet built.** Both are server/security-principal-level DDL
outside the EF relational model; the shipped migration creates an ordinary
clustered index on `(TimestampUtc, Id)` and no grant DDL (see the TODO in
`AuditEventConfiguration`; design.md §14/§16 track it). Once that migration
ships, the app's SQL login gets INSERT/SELECT only on this table —
append-only enforced by the database, not convention. Until then it holds by
application convention only.

---

## Sync (design.md §12)

### SyncOutboxEvent — low side, append-only

| Column | Type | Notes |
|---|---|---|
| Id | bigint identity | |
| SpaceId | uniqueidentifier FK → Space | exported spaces only |
| SequenceNumber | bigint | **gap-free per space** — see below |
| EventType | tinyint | page upsert / move / delete / restore, comment, attachment, labels, restrictions, page properties, page marking |
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
| Embedding | vector(1536) | SQL Server 2025 native type (landed via `AlterPageEmbeddingToNativeVector`; SQLite maps a float blob) — **dimensions are fixed per column**; changing the model is a migration + full re-embed, enforced by a startup guard |
| Model | nvarchar(128) | sanity check against config at query time |
| UpdatedAtUtc | | |

Unique `(PageId, ChunkIndex)`. The DiskANN vector index is deliberately
deferred (see the AlterPageEmbeddingToNativeVector migration doc-comment;
`VECTOR_DISTANCE` is index-blind by documentation and boxed SQL Server
2025's index format makes the table read-only) — search is in-engine exact
`VECTOR_DISTANCE`, tripwire-tested for the day the engine lifts its limits.
On SQLite (tests) this table maps `Embedding` to a blob and the in-memory
cosine fallback handles search.

### GitLabCredential — per-user GitLab PAT, encrypted, instance-local

| Column | Type | Notes |
|---|---|---|
| UserId | uniqueidentifier PK, FK → User | PK = FK (the PageEmbeddingState pattern): one credential per user |
| ProtectedToken | nvarchar(1024) | ASP.NET Data Protection output, never plaintext; never readable via any API |
| CreatedAtUtc / UpdatedAtUtc | datetime2(3) | |

Never synced, never exported (no `SyncEventType`; the outbox classifier has
no case for its events). `ON DELETE NO ACTION` like every FK.

### PageRevisionContributor — immutable

Composite PK `(PageRevisionId FK → PageRevision, UserId FK → User)`; index
`(UserId)` for per-user attribution queries; NO ACTION, never updated or
deleted. One row per user whose live co-editing updates fed the revision
(design.md §8); rows exist only for session saves and are written in the
revision's own transaction. The FK targets PageRevision's PK rather than
its `(PageId, RevisionNumber)` unique index — every FK here targets a PK.
Related notes: `AuditEvent.Channel` gains `6 = realtime` (the hub surface),
and like presence, edit-session state (membership, update log, contributor
marks) is ephemeral hub memory — but unlike presence it IS audited, as
`page.edit_session.joined`/`.left`.

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
`FailedAttempts int`, `FailedRevisionNumber int`, `LastAttemptAtUtc`,
`UpdatedAtUtc`. The embedding
job's trigger is a scan — a page is due when this row is missing or its
`EmbeddedRevisionNumber` differs from `Page.CurrentRevisionNumber` — chosen
over event wiring because it also catches the sync CLI's out-of-process
imports (design.md §9.4) and survives restarts. Purged with the chunk rows
when a page is trashed; the absence re-embeds on restore. Like
`PageEmbedding`: derived, never synced, no audit rows (system action).

`FailedAttempts` / `FailedRevisionNumber` are a *pair*: consecutive failures
and the revision they were counted against. The scan is oldest-first and a
page stays due until it succeeds, so a page the endpoint can never embed
(oversized chunk, provider content filter) would otherwise head every batch
forever. Once `FailedAttempts` reaches the job's `MaxAttempts` **and**
`FailedRevisionNumber` still equals `Page.CurrentRevisionNumber`, the scan
skips the page — quarantined. Both conditions matter: the quarantine is on
the *revision*, so editing the page is the cure, and a failure against a
different revision resets the count to 1 rather than spending the old
budget on new content. Success clears both to 0. Operator-visible through
`rocketwiki.embeddings.pages_quarantined` (§15) and a per-page warning log.

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
8. **Access grants and role grants are two `AccessRule` kinds (3 and 1) with a
   child `AccessRuleSelectors` table**, rather than a separate grants table or a
   packed selector column — one grants query, one check constraint, one audit
   snapshot, and selector values queryable as data (design.md §6.4, §21.15).

## Open items

- [ ] Embedding dimension is written as 1536 — confirm once the model per
      instance is chosen (design.md §17).
- [ ] `DetailsJson` schema per audit action — define alongside each feature.
- [ ] Whether `Space.Key` is immutable after creation (recommended: yes;
      renames touch URLs, sync identity, and audit `SpaceKey`).
