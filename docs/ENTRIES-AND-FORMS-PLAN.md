# Plan: page entries, and forms on top of them

**Status: a proposal, not a decision.** Nothing here is built. This document
exists to make the shape of the work visible before anyone commits to it, and to
record the choices that are expensive to change later. Same voice as the rest of
the repo: what is known, what is a judgement call, and what nobody has decided.

The ask, in one line: *a page can carry **entries** — objects, stored against the
page, each with its own protective marking — and then a ConfiForms alternative on
top, with a macro that renders results into a page.*

---

## The short answer

**An entry is an object with an id, stored against a page, carrying its own
marking.** That is the whole data model. There is no nested key space, no tuple
encoding, no ordered range scan — an earlier draft of this plan proposed a
Deno-KV-shaped key and it has been deliberately dropped. What that buys is not
just less code:

- **an order-preserving tuple encoding is a one-way door.** Get it wrong and
  fixing it is a data migration; it also has to sort identically on SQL Server
  and SQLite, which is exactly the class of cross-provider trap that has already
  bitten this repo twice (`SqlServerFileStorage`'s `BIN2` collation, the page
  property registry's `KeyNormalized`);
- **prefix range scans are a probing instrument.** A store designed for range
  reads makes §6.7's "invisible is indistinguishable from absent" much harder to
  hold, because an attacker can walk the key space. A flat list of objects has a
  far smaller surface;
- **the entry becomes the unit of marking**, which mirrors how pages already work
  — one subject, one marking, resolved once. That is a much better fit for §21
  than classifying values at arbitrary depths in a key hierarchy.

**The remaining hard part is that entries are marked at all.** Today the page is
the unit of classification: `PermissionContextLoader` resolves exactly one
`ProtectiveMarking` per page, `ClearanceGate.Check` is called once, and
everything downstream — the tree walk, search, RQL, the widgets, the analytics
report — inherits that decision and never asks again. Entries mean a page has
several classifications at once, and every place that assumed otherwise has to be
found. That is the risk, and Part 2 is about it.

**The forms layer is the cheap part** and mostly assembly: the fence-macro
pattern is proven, the query language exists, and the record store is the entry
store.

---

## What already exists and is reusable

Concretely, not aspirationally:

- **`ClearanceGate.Check` takes a `ProtectiveMarking` *value*,** not a page. It
  does not care what the marking is attached to. Per-entry marking needs no
  change to the gate — only to how many times it is called and with what.
- **`ProtectiveMarking` is already a value type** (level, eyes-only country set,
  prefix) with a fail-closed constructor: an undefined level normalizes to TOP
  SECRET rather than comparing below every clearance (§21, and the level-0 trap
  recorded there).
- **Aggregate marking machinery already exists** (§21.13). `AggregateMarkingLabel`
  computes the MAX level over a set and a truthful *conjunction* of caveats — the
  exact shape needed for "what is this page's entry set marked, overall". It
  lives in `RocketWiki.Api` specifically so enforcement code cannot consult it.
- **The reserved-fence pattern is proven** (§22, the page-list widget). A fence
  that stays a plain `codeBlock` needs *zero* changes to `fromMarkdown`,
  `toMarkdown`, or the schema — only a branch in `CodeBlockView.tsx`'s if-chain,
  the de-facto reserved-language registry. Verified by a zero-line diff on both
  pipeline files when that widget shipped.
- **RQL exists** (§22): a parser, a bounded grammar, an AST, and — more
  importantly — security rules already argued through and tested.
- **The audit pipeline** (§7) commits its row in the same transaction as the
  change, via `RaiseDomainEvent`.
- **The sync outbox** (§12) carries content across the boundary, and its failure
  modes are now known first-hand (see §2.5).

---

## Part 1 — Entries

### 1.1 The model

An entry is:

- **an id** — a GUID, server-assigned. Not a user-chosen name: a name is a thing
  to collide on, normalize, and get case-folding wrong across two providers, and
  nothing in the forms use case needs one;
- **a collection** — a short string naming what kind of entry this is
  (`incident-report`). This is the one piece of structure beyond the id, and it
  earns its place: without it, "every record of this form on this page" means
  reaching into JSON, and the query that the whole forms feature rests on becomes
  the awkward one. **A page may hold as many collections as it likes** — see
  §1.2;
- **an object** — JSON, an object at the root (not a bare scalar or array), with
  a size cap. Suggest 64 KiB; anything larger is an attachment;
- **a marking** — level, eyes-only country set, prefix. Mirrors `PageMarking`;
- **a version** — changes on every write, so callers can do compare-and-set.

That last one is not gold-plating. Forms have concurrent submitters, and
retro-fitting optimistic concurrency onto a store that shipped without it means
every existing caller is racy in the meantime. The page edit path already has the
pattern (`ExpectedRevisionNumber` → `StaleRevisionError`); reuse the shape rather
than inventing a second one.

### 1.2 Many collections per page

A page holds any number of collections, and a collection holds any number of
entries. An incident page can carry `incident-report`, `action-item` and
`sign-off` side by side, each with its own form definition and its own rendered
table. Nothing in the model constrains a page to one, and the `(PageId,
Collection)` index is what makes several cheap.

Three consequences, and the third is the one that bites:

**A collection's identity is `(page, collection)`, not the name alone.** The same
name on two pages is two independent sets with nothing shared — not one
collection spread across pages. That follows from entries being page-scoped, but
it is the opposite of what "collection" suggests to most people, so the
form-authoring UI should say it rather than let someone discover it. Gathering
one logical collection across pages is the cross-page query that §1.3 puts out of
v1.

**A collection name is unique within its page.** Two `form-definition` fences on
one page naming the same collection is a conflict, not a merge — they would
compete to define the fields of one record set. Refuse it at parse time, in the
editor, with both locations named. Silently letting the last one win is how a
form quietly changes shape.

**Enumerate a page's collections from its definitions, never from its entries.**
This is a §6.7 trap that only appears once a page can have several. `SELECT
DISTINCT Collection WHERE PageId = @p` is the obvious implementation and it
leaks: it reveals that a collection has *at least one entry*, to a reader who may
be permitted to see none of them. The definitions are page content and visible to
anyone who can read the page, so they are the safe source. An empty collection
and one whose entries are all above your clearance must look identical.

### 1.3 Scoping to pages

Every entry belongs to exactly one page, which gives three things for free:

- **a permission anchor** — `canView` on the page is the outer gate before any
  entry-level check runs;
- **a lifecycle** — trashing a page trashes its entries, restoring restores them.
  Soft delete, for the same reasons page deletes are;
- **a sync unit** — entries travel with their page.

The cost: there is no cross-page entry space. "Every incident report in this
space" is a spanning query, which is a permission-filtered read with all of
§2.2's constraints, and it is deliberately out of v1.

### 1.4 The proposed table

| Column | Notes |
|---|---|
| `Id` | GUID v7, server-assigned |
| `PageId` | FK; soft-deletes with the page |
| `Collection` | short string, indexed with `PageId`; the forms lookup |
| `Data` | JSON object, capped |
| `Version` | changes on every write; the concurrency token |
| `MarkingLevel`, `MarkingPrefix` | mirrors `PageMarking` |
| `UpdatedAtUtc`, `UpdatedByUserId` | `UpdatedByUserId` null for sync-applied rows, as `PageProperty` already does |

Plus a child table for the eyes-only country set, exactly as `PageMarkingCountry`
is to `PageMarking`. **Do not** flatten the country set into a delimited string:
that table exists because a set needs set semantics, and a second representation
of a marking is a second thing to get wrong.

Index on `(PageId, Collection)`. That is the only read pattern v1 needs.

**One collation note survives the simplification.** `Collection` is a string used
for lookup, and SQL Server's default collation is case-*insensitive* while
SQLite's is case-*sensitive* for ASCII — so `incident-report` and
`Incident-Report` are one collection in production and two in the test tier.
Normalize it on write (lowercase, invariant) and store the normalized form, the
same fix the page-property registry already carries. A test should assert two
case-differing collections behave identically on *both* providers.

---

## Part 2 — Per-entry markings: the actual work

This is the section to argue with before anything is built.

### 2.1 The invariant that changes

Today: **one page, one marking, one gate call, and the answer is inherited
everywhere.** A page you can see is a page whose every part you can see.

With per-entry markings that stops being true. The compiler can help, in the way
§21 already used: when markings were introduced, `Compute`/`Explain` took a
*required* `ProtectiveMarking` parameter specifically so the compiler enumerated
every call site — there turned out to be two. The same trick applies: make the
entry read path require a per-entry marking argument that cannot be defaulted, so
no call site can quietly skip it.

### 2.2 A pruned entry must be indistinguishable from an absent one

§6.7 is not advisory here. If a reader cannot see an entry, they must not be able
to detect that it exists. Concretely that forbids:

- **counts** — "showing 8 of 12" is a census of the classified estate, the same
  argument that keeps classification out of RQL (§22.3), telemetry dimensions
  (§21.8) and the analytics report (§7). No total, no "some results hidden"
  badge;
- **offset-based pagination** — a cursor encoding an absolute offset gives two
  readers with different clearances inconsistent pages, and comparing them
  reveals the difference. Cursors must be keyed (last-id-seen), not positional;
- **visible ordinals** — if the UI numbers rows 1..n, it numbers what the reader
  can see, never the underlying position.

The page-list widget already got this right for pages and is the precedent.

### 2.3 The entry is the unit — fields are not separately classified

An entry is one object with one marking. A field inside it cannot be marked
differently from its siblings.

This is a deliberate limit and it is the right one. Field-level marking would
mean redacting *within* an object — returning a partial object to some readers —
and a partial object is indistinguishable from a complete one to the code that
consumes it. Every consumer would have to know which fields might be missing, and
nothing would enforce that they did. If two facts need different classifications,
they are two entries.

The corollary worth stating: **a collection name is visible to anyone who can see
the page**, because the form definition that names it is page content. So a
collection name must not itself be sensitive. That is a documentation rule rather
than an enforced one, and it should be said out loud in the form-authoring UI.

### 2.4 A page's own marking versus its entries'

Two rules, and the second is the one people forget:

1. **An entry may not be marked below its page.** The page is the container; a
   reader who cannot open the page never reaches the entry, so a lower marking is
   a claim that will eventually be believed by someone.
2. **An entry may be marked *above* its page — and then the page's displayed
   marking must account for it.** This is where `AggregateMarkingLabel` earns its
   keep: the banner should reflect the max over the page and the entries *that
   reader can see*. Per-viewer, exactly as the page-list widget's aggregate
   already is, because the same page legitimately shows UK OFFICIAL to one reader
   and UK SECRET to another.

The existing downgrade rule carries over unchanged: **you may not set a marking
you could not then read** (§21.6), enforced per entry.

### 2.5 What this does to search, RAG and sync

- **Search and embeddings:** if entry contents are indexed, the post-filter must
  run per entry, not per page. The honest v1 is **do not index entries at all** —
  say so, and revisit deliberately.
- **Ask/RAG (§9):** if an entry can enter model context, the answer's aggregate
  marking must include it. §21.13 already established that the aggregate covers
  everything that *entered* the context, cited or not. The cheap and honest v1 is
  to keep entries out of retrieval.
- **Sync (§12):** entries are content and must travel; a new `SyncEventType`. Two
  lessons from this repo's own history apply directly. First, an upsert payload
  that *omits* a field must not be read as *clearing* it — that exact bug shipped
  for page icons and silently stripped them on every incremental sync, because
  the import assigned the field unconditionally while its neighbours handled
  absence deliberately. Second, markings must arrive fail-closed: an entry
  arriving with no marking is TOP SECRET, not OFFICIAL, exactly as
  `ApplyPageMarkingAsync` already does for pages.

---

## Part 3 — The ConfiForms alternative

ConfiForms is three things: a **form definition**, a set of **records**, and
**views** that filter and render them. Map each onto machinery that exists.

### 3.1 Form definition — a fence, not a table

Define the form in a reserved fence on the page:

~~~
```form-definition
collection = incident-report
field = severity: select(low, medium, high), required
field = summary: text, required
field = occurredAt: date
```
~~~

Why a fence rather than a database table:

- it **round-trips as text**, so the definition is versioned by the page's own
  revision history, diffable and restorable — for free;
- it needs **no schema migration** when the field vocabulary grows;
- it inherits the proven pipeline property: a reserved fence that stays a plain
  `codeBlock` touches nothing in the Markdown round-trip.

The trade-off, stated plainly: a fence is not queryable by the server without
parsing page content, so validation of a submitted record happens where the
definition is parsed. Put that parser in Core so server and SPA share one
grammar — the RQL precedent (one grammar, server-side, the SPA deliberately not
re-parsing) is the right shape.

### 3.2 Records — entries in a collection

A record is an entry with `Collection = <the form's collection>` and `Data` = the
field map. This gives, with no new storage:

- listing a form's records — the `(PageId, Collection)` index;
- per-record markings, which is the actual reason this is worth building on
  entries rather than a bespoke records table;
- compare-and-set on create;
- lifecycle and sync tied to the page.

### 3.3 The display macro

A second reserved fence, following the page-list widget exactly:

~~~
```form-list
collection = incident-report
where = severity IN (high, medium) AND occurredAt > now("-30d")
columns = occurredAt, severity, summary
```
~~~

**Reuse RQL's grammar and its security rules.** Not just the parser — the rules.
Fields that are classification or permission state are refused *by name* with a
distinct error code, because a filter over classification is a census. That
argument does not weaken because the subject is a form record rather than a page;
if anything a record is a sharper instrument, since the author controls the field
vocabulary.

Rendering follows the widget precedent: permission-filtered server-side,
per-viewer aggregate marking on the block, no counts, no "hidden rows"
affordance.

A page carries as many of these as it has collections — an incident page might
define `incident-report`, `action-item` and `sign-off`, and render three tables
between its prose. Each fence names its own collection and is parsed
independently, so one malformed fence degrades to the existing
`FenceIncomplete` state without taking the others down. Two questions the
multiplicity raises, both worth deciding rather than discovering:

- **the per-viewer aggregate marking is per block, not per page.** Three tables
  drawn from three collections can legitimately carry three different labels, and
  a reader needs to know which table a marking describes. The page's own banner
  is the max over everything visible (§2.4); a block's is the max over that
  block's rows;
- **a `form-list` may name a collection this page has no definition for.** That
  is not an error — a page could render a collection defined elsewhere once
  cross-page queries exist — but in v1 it can only ever be empty, so it should
  say "no such collection on this page" rather than showing an empty table that
  looks like "no records yet". Those two states are different facts and a reader
  will act on them differently.

### 3.4 What a v1 should not be

- **No workflow.** ConfiForms has state transitions and actions; that is
  `PLATFORM-PLAN.md`'s rule engine, not this. If both get built they share the
  engine — do not grow a second one here.
- **No email or webhook on submit.** That is automation, and the platform plan
  already decided automation runs as a defined principal, never as the system.
- **No cross-page record queries in v1.** Scope to the page the fence is on.
- **No file-upload fields.** Attachments have their own storage, quota and
  scanning story; a form field that smuggles a blob past it is a hole.
- **No field-level markings.** See §2.3 — if two facts need different
  classifications, they are two entries.

---

## Phasing

Each phase should be independently shippable and independently abandonable.

1. **Entries, inheriting the page's marking.** No per-entry marking yet.
   Delivers the table, the collection index, objects, versions, audit, sync.
   Proves the collation behaviour on both providers.
2. **Per-entry markings.** The Part 2 work: required-parameter gate,
   prune-not-count reads, keyed cursors, per-viewer aggregate, fail-closed
   import. The riskiest phase; do not merge it with anything else.
3. **Form definition fence + submit.** Parser in Core, editor affordance,
   server-side validation, records as entries.
4. **`form-list` macro.** RQL-subset filtering, table rendering, per-viewer
   aggregate marking.
5. **Only then:** cross-page queries, indexing entries for search, exports.

---

## Decide these before writing code

1. **May an entry be marked above its page?** — recommendation: yes, with a
   per-viewer aggregate banner (§2.4). Saying no is simpler and much less useful.
2. **What happens to page properties (§20)?** Two object-ish stores on pages is
   one too many. Either draw a bright line (properties are admin-vocabulary human
   metadata, shown beside the page; entries are application data) or make
   properties a projection over entries and retire their tables. Recommendation:
   **leave them alone in v1**, revisit once forms are real — a migration is cheap
   later and a wrong unification is not.
3. **Are entries searchable?** — recommendation: no in v1, deliberately.
4. **Do entries reach the assistant?** — recommendation: no in v1.
5. **Size cap, per-page entry cap, and per-page collection cap.** A store with
   no quota is a denial-of-service surface and a sync-payload problem, and now
   that a page can hold many collections there are two dimensions to bound, not
   one.
6. **Does a record's author see their own record when it is marked above their
   clearance?** A genuine question with no obvious answer — someone can submit a
   form into a marking they cannot then read. §21.6's "may not set a marking you
   could not then read" suggests refusing the submission, which may be the wrong
   answer for a form whose whole purpose is one-way reporting.
7. **Is `Collection` free-form or a registry?** Free-form is simpler and matches
   the fence-defines-the-form model. A registry (like page property keys) buys
   validation and discoverability at the cost of an admin screen and a migration
   story. Recommendation: free-form in v1, normalized on write.

---

## Risks

- **The marking model is the risk, not the store.** Sub-page classification
  touches the tree, search, RAG, sync, aggregate labels and the audit story at
  once. Phases 1 and 2 must stay separate so entries can ship while the marking
  model is still being argued about.
- **Scope creep toward workflow.** ConfiForms users will ask for state machines
  within a week. The answer is the platform plan's engine, or "not yet" — never a
  second engine here.
- **Two object stores.** If page properties and entries both live for long
  without a stated rule for which is which, both will be used for both, and the
  migration will be worse than doing it now.
- **`Data` is schemaless and the form definition is page content**, so a form can
  be edited out from under its records. Decide whether old records are shown
  as-stored (recommended) or coerced to the current definition (a silent rewrite
  of history).
- **Follow-up from the protective-marking overhaul (September 2026): page
  entries share the marking gate but have no selector storage yet.** An entry's
  marking goes through the same `MarkingGate` as a page's (level, selector
  eligibility and grant, caveat — design.md §21.15), but `PageEntries` carries
  only level, prefix and countries: an entry cannot carry an additional selector
  of its own — `PageEntryService` refuses an entry marking that names one with
  a `ValidationError` rather than silently dropping it — and the bundle
  importer applies only the level, prefix and countries of an entry payload's
  marking. Nothing widens today, because an entry is reachable only through its
  page, whose selectors already gate it. Phase 2 above must add a
  `PageEntrySelectors` child table mirroring `PageMarkingSelectors` (PK
  `(PageEntryId, Category)`), put it on `ToMarking()`, and make the importer
  apply it — before an entry can be marked above its page in any dimension
  other than level.
