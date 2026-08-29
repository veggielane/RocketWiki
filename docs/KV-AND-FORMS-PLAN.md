# Plan: a page-scoped key/value store, and forms on top of it

**Status: a proposal, not a decision.** Nothing here is built. This document
exists to make the shape of the work visible before anyone commits to it, and to
record the choices that are expensive to change later. Same voice as the rest of
the repo: what is known, what is a judgement call, and what nobody has decided.

The ask, in one line: *a Deno-KV-shaped store with nested keys, scoped to pages,
where every value carries its own protective marking — and then a ConfiForms
alternative on top, with a macro that renders results into a page.*

---

## The short answer

**The key/value store is the easy half.** Nested keys, prefix reads, atomic
writes: that is a table, an encoding, and a service. Two weeks of ordinary work
on infrastructure that already exists.

**Per-value protective markings are the hard half, and they are not a feature —
they are a change to an invariant the whole system currently rests on.** Today
the page is the unit of classification. `PermissionContextLoader` resolves
exactly one `ProtectiveMarking` per page; `ClearanceGate.Check` is called once
per page; `PageMarking` is one row per page. Everything downstream — the tree
walk, search, RQL, the page-list widget, the analytics report — inherits that
single decision and never asks again.

Marking individual values means a page has *many* classifications at once. That
is a real capability and worth wanting. It is also the point at which several
existing guarantees stop being automatic and have to be re-established
deliberately. Most of this document is about that, because that is where the
cost and the risk actually live.

**The forms layer is the cheapest part** and mostly assembly: the fence-macro
pattern is proven, the query language exists, and the record store is the KV
store. If the KV and marking work is done properly, ConfiForms-alike is a
straightforward build on top.

---

## What already exists and is reusable

Concretely, not aspirationally:

- **`ClearanceGate.Check` takes a `ProtectiveMarking` *value*,** not a page. It
  does not care what the marking is attached to. Per-value marking needs no
  change to the gate itself — only to how many times it is called and with what.
- **`ProtectiveMarking` is already a value type** (level, eyes-only country set,
  prefix) with a fail-closed constructor: an undefined level normalizes to TOP
  SECRET rather than comparing below every clearance (§21, and the level-0 trap
  recorded there).
- **Aggregate marking machinery already exists** (§21.13). `AggregateMarkingLabel`
  computes the MAX level over a set and a truthful *conjunction* of caveats — the
  exact shape needed for "what is this page's KV set marked, overall". It already
  lives in `RocketWiki.Api` specifically so enforcement code cannot consult it.
- **The reserved-fence pattern is proven** (§22, the page-list widget). A fence
  that stays a plain `codeBlock` needs *zero* changes to `fromMarkdown`,
  `toMarkdown`, or the schema — only a branch in `CodeBlockView.tsx`'s if-chain,
  which is the de-facto reserved-language registry. That was verified by a
  zero-line diff on both pipeline files. A form-render macro is the same shape.
- **RQL exists** (§22): a parser, a bounded grammar, an AST, and — more
  importantly — a set of security rules already argued through and tested.
- **The audit pipeline** (§7) commits its row in the same transaction as the
  change, via `RaiseDomainEvent`.
- **The sync outbox** (§12) carries content across the boundary, and its
  failure modes are now known first-hand (see "Sync" below).

---

## Part 1 — The key/value store

### 1.1 Keys

Deno KV's model, adopted nearly wholesale because it is well-designed and
familiar: **a key is an ordered array of parts**, each part a string, number,
boolean, or byte string. `["form", formId, "record", 42]`. Reads are by exact
key or by **prefix range** over the ordered key space.

The part worth thinking about is the encoding, because SQL has no native tuple
ordering. A key needs a canonical single-column encoding that sorts identically
to the tuple order, so a prefix read is one indexed range scan rather than a
join per part:

- type-tagged parts, so `1` and `"1"` never collide;
- order-preserving numeric encoding (fixed-width, sign-corrected), so `2 < 10`;
- an unambiguous separator with escaping, so `["a", "b"]` and `["a\0b"]` differ.

Store both forms: `KeyEncoded` (the sortable canonical bytes, indexed) and
`KeyParts` (JSON, for round-tripping to the API without decoding). The encoded
form is the contract; a change to it is a data migration, so it gets a version
byte from day one.

**Collation is a trap this repo has already been bitten by twice.** SQL Server's
default collation is case-*insensitive*, SQLite's is case-*sensitive* for ASCII
— so `["Owner"]` and `["owner"]` are the same key in production and different
keys in the test tier. Both the `SqlServerFileStorage` blob key (`BIN2`) and the
page-property registry (`KeyNormalized`) already carry scars from this. `KeyEncoded`
must be a binary-collated column, and a test must assert two case-differing keys
stay distinct on *both* providers.

### 1.2 Scoping to pages

Every entry belongs to exactly one page. The page is the namespace, which gives
three things for free:

- **A permission anchor.** `canView` on the page is the outer gate before any
  value-level check runs.
- **A lifecycle.** Trashing a page trashes its entries; restoring restores them.
  (Deleting must be a soft delete for the same reason page deletes are.)
- **A sync unit.** Entries travel with their page.

The cost: there is no cross-page key space. `list(["form", id])` is scoped to one
page unless a query layer explicitly spans pages, and that spanning query is a
permission-filtered read like any other — see §2.3.

### 1.3 Values and versions

Values are JSON, with a size cap (suggest 64 KiB; large content belongs in an
attachment). Each entry carries a **version stamp** that changes on every write,
so the API can offer Deno-KV-style atomic `check`-and-`set`. That is not
gold-plating: forms with concurrent submitters need it, and retro-fitting
optimistic concurrency onto a store that shipped without it means every existing
caller is racy in the meantime. The page edit path already has this pattern
(`ExpectedRevisionNumber` → `StaleRevisionError`); reuse the shape.

### 1.4 The proposed table

| Column | Notes |
|---|---|
| `PageId` | FK, cascade with the page's soft delete |
| `KeyEncoded` | `varbinary`, binary collation, indexed with `PageId` |
| `KeyParts` | JSON, the round-trip form |
| `Value` | JSON, capped |
| `Version` | changes on every write; the concurrency token |
| `MarkingLevel`, `MarkingPrefix` | mirrors `PageMarking` |
| `UpdatedAtUtc`, `UpdatedByUserId` | `UpdatedByUserId` null for sync-applied rows, as `PageProperty` already does |

Plus a child table for the eyes-only country set, exactly as `PageMarkingCountry`
is to `PageMarking`. **Do not** flatten the country set into a delimited string:
the existing table exists because a set needs set semantics, and a second
representation of a marking is a second thing to get wrong.

Primary key `(PageId, KeyEncoded)`. The prefix read is
`WHERE PageId = @p AND KeyEncoded >= @lo AND KeyEncoded < @hi`.

---

## Part 2 — Per-value markings: the actual work

This is the section to argue with before anything is built.

### 2.1 The invariant that changes

Today: **one page, one marking, one gate call, and the answer is inherited
everywhere.** A page you can see is a page whose every part you can see.

With per-value markings that stops being true, and every place that assumed it
has to be found. The compiler can help here in the way §21 already used: when
markings were introduced, `Compute`/`Explain` took a *required* `ProtectiveMarking`
parameter specifically so the compiler enumerated every call site. The same
trick applies — make the KV read path require a per-entry marking argument that
cannot be defaulted, so no call site can quietly skip it.

### 2.2 A pruned value must be indistinguishable from an absent one

§6.7 is not advisory here. If a reader cannot see an entry, they must not be able
to detect that it exists. Concretely that forbids:

- **gaps in ordering or indices** — a list that returns records 1, 2, 4 says
  what happened to 3;
- **counts** — "showing 8 of 12" is a census of the classified estate, the same
  argument that keeps classification out of RQL (§22.3), telemetry dimensions
  (§21.8) and analytics (§7). No total, no "some results hidden" badge, no
  pagination cursor whose arithmetic reveals skipped rows;
- **stable cursors that skip** — if a cursor encodes an absolute offset, two
  readers with different clearances get inconsistent pages, and comparing them
  reveals the difference. Cursors must be *keyed* (last-key-seen), not
  offset-based.

The page-list widget already got this right for pages and is the precedent to
follow.

### 2.3 The key is as classified as its value

**Decide this explicitly; the default is not obvious and the wrong default is a
disclosure channel.**

A key part can carry meaning: `["review", "alice", "outcome"]`. If a reader who
cannot read the *value* can still enumerate the *key*, prefix listing becomes an
oracle — and a patient one, since a KV store is designed for range scans.

The recommendation is: **you cannot see the key of a value you cannot read.**
Prefix reads return only entries that pass the gate, keys included. The cost is
that a key is not a reliable "does this exist" check for anyone, which is
correct: the alternative makes existence a side channel.

The counter-argument worth hearing: forms may want a fixed schema of field keys
that everyone knows, with only *values* classified. That is a legitimate design,
but it should be expressed as *the form definition is public and the records are
classified* — not as "keys leak". Keep the distinction.

### 2.4 A page's own marking versus its values'

Two rules, and the second is the one people forget:

1. **A value may not be marked below its page.** A page is the container; a
   reader who cannot open the page never reaches the value, so a value marked
   lower is a lie that will eventually be believed.
2. **A value may be marked *above* its page — and then the page's displayed
   marking must account for it.** This is where `AggregateMarkingLabel` earns
   its keep: the banner a reader sees should reflect the max over the page and
   the values *that reader can see*. Per-viewer, exactly as the page-list widget's
   aggregate already is, because the same page legitimately shows UK OFFICIAL to
   one reader and UK SECRET to another.

And the existing downgrade rule carries over unchanged: **you may not set a
marking you could not then read** (§21.6), enforced per value.

### 2.5 What this does to search, RAG and sync

- **Search and embeddings:** if KV values are indexed, the post-filter must run
  per value, not per page. The safest v1 is **do not index KV values at all** —
  say so, and revisit deliberately.
- **Ask/RAG (§9):** if a KV value can enter model context, the answer's aggregate
  marking must include it. §21.13 already established that the aggregate covers
  everything that *entered* the context, cited or not. Again, the cheap and
  honest v1 is to keep KV out of retrieval.
- **Sync (§12):** entries are content and must travel; a new `SyncEventType`.
  Two lessons from this repo's own history apply directly. First, an upsert
  payload that *omits* a field must not be read as *clearing* it — that exact
  bug shipped for page icons and silently stripped them on every incremental
  sync. Second, markings must arrive fail-closed: an entry arriving with no
  marking is TOP SECRET, not OFFICIAL, exactly as `ApplyPageMarkingAsync` already
  does for pages.

---

## Part 3 — The ConfiForms alternative

ConfiForms is three things: a **form definition**, a set of **records**, and
**views** that filter and render them. Map each onto machinery that exists.

### 3.1 Form definition — a fence, not a table

Define the form in a reserved fence on the page:

~~~
```form-definition
id = incident-report
field = severity: select(low, medium, high), required
field = summary: text, required
field = occurredAt: date
```
~~~

Why a fence rather than a database table:

- it **round-trips as text**, so the definition is versioned by the page's own
  revision history, diffable, and restorable — for free;
- it needs **no schema migration** when the field vocabulary grows;
- it inherits the proven pipeline property: a reserved fence that stays a plain
  `codeBlock` touches nothing in the Markdown round-trip.

The trade-off, stated plainly: a fence is not queryable by the server without
parsing page content, so *validation* of a submitted record must happen where the
definition is parsed. Put that parser in Core so the server and the SPA share one
grammar — the RQL precedent (one grammar, server-side) is the right instinct, and
the page-list widget's rule that the SPA does *not* re-parse is the right shape.

### 3.2 Records — KV entries

A record is a KV entry at `["form", <formId>, <recordId>]`, value = the field
map. This gives, with no new storage:

- prefix listing of a form's records;
- per-record markings — which is the actual reason this is worth building on KV
  rather than a records table;
- atomic create via version `check`;
- lifecycle and sync tied to the page.

### 3.3 The display macro

A second reserved fence, following the page-list widget exactly:

~~~
```form-list
form = incident-report
where = severity IN (high, medium) AND occurredAt > now("-30d")
columns = occurredAt, severity, summary
```
~~~

**Reuse RQL's grammar and its security rules.** Not just the parser — the rules.
Fields that are classification or permission state are refused *by name* with a
distinct error code, because a filter over classification is a census. That
argument does not weaken because the subject is a form record rather than a page;
if anything a form record is a sharper instrument, since the author controls the
field vocabulary.

Rendering follows the widget precedent: permission-filtered server-side,
per-viewer aggregate marking on the block, no counts, no "hidden rows" affordance.

### 3.4 What a v1 should not be

- **No workflow.** ConfiForms has state transitions and actions; that is
  `PLATFORM-PLAN.md`'s rule engine, not this. If both get built, they share the
  engine — do not grow a second one here.
- **No email/webhook on submit.** That is automation, and the platform plan
  already decided automation runs as a defined principal, never as the system.
- **No cross-page record queries in v1.** Scope to the page the fence is on.
  Spanning pages is a permission-filtered read with all of §2.2's constraints and
  deserves its own decision.
- **No file upload fields.** Attachments have their own storage, quota and
  scanning story; a form field that smuggles a blob past it is a hole.

---

## Phasing

Each phase should be independently shippable and independently abandonable.

1. **KV store, single marking per page.** Entries inherit the page's marking, no
   per-value marking yet. Delivers nested keys, prefix reads, versions, audit,
   sync. Proves the encoding and the collation behaviour on both providers.
2. **Per-value markings.** The §2 work: required-parameter gate, prune-not-count
   reads, keyed cursors, aggregate over the visible set, fail-closed import.
   The riskiest phase; do not merge it with anything else.
3. **Form definition fence + submit.** Parser in Core, editor affordance,
   server-side validation, records as KV entries.
4. **`form-list` macro.** RQL-subset filtering, table rendering, per-viewer
   aggregate marking.
5. **Only then**: cross-page queries, indexing values for search, exports.

---

## Decide these before writing code

1. **Is a key as classified as its value?** (§2.3) — recommendation: yes.
2. **May a value be marked above its page?** — recommendation: yes, with a
   per-viewer aggregate banner. Saying no is simpler and much less useful.
3. **What happens to page properties (§20)?** Two key/value features on pages is
   one too many. Options: leave them alone and draw a bright line (properties are
   admin-vocabulary human metadata; KV is application data); or make properties a
   projection over KV and retire their tables. Recommendation: **leave them alone
   in v1**, revisit once forms are real — a migration is cheap later and a wrong
   unification is not.
4. **Are KV values searchable?** — recommendation: no in v1, deliberately.
5. **Do KV values reach the assistant?** — recommendation: no in v1.
6. **Value size cap and per-page entry cap.** A KV store with no quota is a
   denial-of-service surface and a sync-payload problem.
7. **Does a record's author see their own record when it is marked above their
   clearance?** A genuine question with no obvious answer — someone can submit a
   form field into a marking they cannot then read. §21.6's "may not set a
   marking you could not then read" suggests refusing the submission, which may
   be the wrong answer for a form whose whole purpose is one-way reporting.

---

## Risks

- **The invariant change is the risk.** Sub-page classification touches the tree,
  search, RAG, sync, aggregate labels and the audit story at once. Phase 1 and
  Phase 2 must stay separate so the store can ship while the marking model is
  still being argued about.
- **Prefix reads are a probing instrument.** A KV store is *designed* for range
  scans, which makes §6.7 harder to hold than it is for pages. The keyed-cursor
  and no-count rules are not polish; they are the feature working.
- **The key encoding is a one-way door.** Get the version byte and the binary
  collation in from the first migration.
- **Scope creep toward workflow.** ConfiForms users will ask for state machines
  within a week. The answer is the platform plan's engine, or "not yet" — not a
  second engine here.
- **Two key/value stores.** If page properties and KV both live for long without
  a stated rule for which is which, both will be used for both, and the migration
  will be worse than doing it now.
