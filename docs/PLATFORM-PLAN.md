# Platform plan: adding issue tracking

**Status: a proposal, not a decision.** Nothing here is built. This document
exists to make the shape of the work visible before anyone commits to it, and
to record the handful of choices that are expensive to change later. It is
written in the same voice as the rest of the repo: what is genuinely known,
what is a judgement call, and what nobody has decided yet.

The question: *what would it take to turn RocketWiki into a platform that also
replaces Jira?*

## The short answer

Roughly **70% of the cross-cutting machinery is reusable with mechanical
renames**, because the pure decision logic was written without entity
dependencies. `EffectivePermissionCalculator.Compute` never sees a `Page` — it
takes a bag of grants, a bag of restrictions, a marking, and a principal.
`ClearanceGate.Check` takes a `ProtectiveMarking` *value*. The Markdown
round-trip is string-in, string-out. None of that needs rethinking; it needs
renaming.

The remaining 30% is genuinely page-shaped and needs design: the **search
index** (full-text is physically `ON Pages(Title, CurrentContent)`) and
**sync** (the gap-free sequence lives on `Space.LastOutboxSequence`).

The tracker *domain* — issues, workflows, boards — is almost entirely new code,
but it is ordinary application code sitting on infrastructure that already
exists and is already compliance-reviewed.

## The pivotal decision: a Project **is** a Space

Make `Project` a `Space` with a kind discriminator, rather than a parallel
entity. This single choice collapses four of the hard problems into mechanical
ones, because everything container-scoped keeps working untouched:

- **Permissions** — `SpaceGrant` + `SpaceRole` (Viewer/Editor/SpaceAdmin) are
  entirely page-independent already.
- **Sync** — `IsExported`, `OriginInstanceId`, `LastOutboxSequence` are the
  outbox's whole contract. A separate root means a second gap-free sequence, a
  manifest carrying two range dictionaries, and a bundle format bump.
- **Replica read-only** — `IsReplicaOf()` feeds the calculator as a bare
  `bool`. Issues inherit the invariant for free.
- **Audit** — `AuditEvent.SpaceKey` denormalization keeps working.
- **Labels and watches** — both already container-scoped.

`Space` has exactly **two** page-specific members: `HomepageId`/`Homepage` and
the `Pages` navigation. Both are additive, neither is behaviour.

If instead Project is a separate root, sync alone becomes a project in its own
right. Recommendation: one container type, `SpaceKind { Wiki, Tracker }`, and
accept that "space" is now the platform's word for a container.

## What you already have

| Concern | Reusability | Work |
|---|---|---|
| Markdown round-trip, chunker, snippets, mention parser | Generic — no `Page` reference anywhere | ~none |
| Clearance gate / protective marking *enforcement* | Generic — takes a value, not a subject | ~none |
| Permission *calculator* | Generic — never sees a `Page` | ~none |
| Audit transport | `(SubjectType, SubjectId)` already polymorphic; no `PageId` column | One enum member, no migration |
| MCP tools | 4 read-only tools; new ones are additive | Low |
| Property registry (§20) | `PagePropertyKey` is already subject-agnostic — this is 80% of custom fields | Low |
| Marking storage (§21) | 1:1 by PK; needs a sibling table | Low |
| Real-time presence / CRDT | Page-keyed but **ephemeral** — no table, no migration | Rename only |
| Comments, attachments, labels, watches, notifications | Five parallel FKs | Moderate, mechanical |
| Access-rule *storage* | Two kinds + a check constraint pinning the shape | Moderate |
| GraphQL schema | ~127 `Page` occurrences; additive but voluminous | Moderate–high |
| **Search + embeddings** | FTS index is physically on `Pages` | **High** |
| **Sync** | Gap-free sequence per Space | **High** (low if Project is a Space) |

The decision logic is far better factored than the data model. That is the
expensive half to get right, and it is already done.

## The seam: `PermissionSubject`

The abstraction already exists and is one rename away:

```csharp
internal readonly record struct PermissionSubject(Guid PageId, Guid SpaceId, string AncestorPath)
```

It is `internal`, so widening it costs nothing externally. Rename to
`(Guid SubjectId, Guid ContainerId, string AncestorPath)` and a flat issue
passes `AncestorPath = "/"` — which `ParseAncestorIds` already returns an empty
array for. **A flat subject falls out of the existing code with no new branch**,
because the calculator consumes an ordered list and a flat subject is simply a
chain of length one.

## What is genuinely new

Everything below is application code with no existing analogue:

- **Issue** — type, status, priority, assignee, reporter, dates, estimate.
  Description reuses the Markdown pipeline unchanged.
- **Workflow engine** — states, transitions, guards. This is the actual core of
  a tracker and the largest new component. Transition permissions are a *new
  axis*: `PageAction` currently has two members (`View`, `Edit`), and a tracker
  needs at least `Transition` and `Assign`.
- **Issue links** — blocks / relates / duplicates. See the leak section below;
  this is not the simple feature it appears to be.
- **Boards** — kanban columns mapped to workflow states, drag to transition.
- **Sprints / backlog** — only if you want Scrum; Kanban alone is a coherent v1.

Custom fields are mostly *not* new: the §20 property registry is admin-governed
key/value with a normalized-key unique index, which is what a custom-field
registry is. Reuse it rather than building a second one.

## The export-control work with no wiki equivalent

This is the part a generic Jira-replacement plan would miss, and it is where
this product's difficulty actually lives.

**Issue links are a new leak channel.** A page links to a page by id, and the
target resolves through `canView` — absent if you cannot see it, per §6.7. But
a tracker renders *"blocked by ENG-412"* as a first-class relationship, and
that string discloses both the existence and usually the summary of an issue
the viewer may not be cleared to see. Every link list must be
permission-filtered per item, and — the subtle half — **link counts must not
betray the filtered items either**. A "3 blockers" badge over two visible rows
is the same leak in aggregate form. §6.7's "not shown as a redacted row, and
not implied by a count" already says this; it just has to be honoured in a
surface that did not exist when it was written.

**Board columns and counts are the same problem at scale.** A swimlane header
reading "In Progress (14)" over eight visible cards leaks six. Either counts are
computed post-filter, or they are not shown.

**Issue titles are the primary leak surface.** Link pickers, autocomplete,
`@`-mention of an issue, transition dialogs, notification bodies — each renders
a title, and each needs the same gate the page tree already applies.

**Markings apply per issue and do not inherit through links.** An issue that
references a `UK SECRET` page is not thereby SECRET, and an issue *whose
description quotes* that page probably is. That is a human judgement the system
cannot make; what the system must do is ensure every issue carries a marking
(§21's "every page must be marked" applies verbatim), default it from the
project, and refuse to let an author mark above their own clearance.

**Aggregate markings already exist and apply directly** (§21.13). A board
column, a filter result, a sprint — each is a compilation and should carry the
highest classification among its members, with caveats conjoined rather than
merged. The label machinery is built; it needs new call sites, not new logic.

**Assignment is a disclosure.** Assigning an issue to someone who cannot view
it either fails closed or silently creates an unactionable item. Decide which;
fail-closed with a typed error is consistent with the rest of the system.

## Phasing

Each phase leaves a working, shippable product. No phase requires the next one
to be useful.

**Phase 0 — the seam (no user-visible change).**
Rename `PermissionSubject` and the enum family (`PageAction` → a subject-neutral
action, `SpaceRole` → container role). Because these are tinyint-backed, **the
stored values do not move and there is no data migration** — it is a
compile-time rename. Widen `AuditSubjectType`. The ~1400 existing tests are the
safety net: this phase must change no behaviour, and the composition invariants
(§21) must still hold. De-risks everything after it.

**Phase 1 — container + issue, no workflow.**
`SpaceKind` discriminator. `Issue` with title, description, type, priority.
Permissions, markings, properties, comments, attachments, labels, watches,
audit. At the end of this phase you have a working (if primitive) tracker whose
access control, classification and audit are the same reviewed machinery the
wiki uses.

**Phase 2 — workflow.**
States, transitions, guards, assignment. New permission verbs. Transition
history in the audit log — a state change is exactly the kind of thing §7
exists to record.

**Phase 3 — links and boards.**
Issue links with the permission-filtered rendering described above. Kanban board
over workflow states. Aggregate markings on columns.

**Phase 4 — search federation.** *The hard one.*
A second FTS index, a second embedding pipeline, and a cross-type ranking
decision (two independent RANK scales that must be reconciled). `SearchHit.page`
is currently non-null, so federated results are a **breaking schema change** —
likely `union SearchSubject = Page | Issue`. Ask-the-wiki and MCP extend
naturally once this exists.

**Phase 5 — sync.**
If Project is a Space, this is new `SyncEventType` members and new payload arms.
If not, it is a second outbox and a bundle format bump.

## Decide these before writing code

1. **Is a Project a Space?** Everything above assumes yes. Deciding no is
   legitimate but changes the cost of Phases 1 and 5 substantially.
2. **The denial-reason format.** `restriction:{PageId}:{RuleId}` is a contract
   consumed by audit rows and the permission inspector. An issue rule leaving
   `PageId` null silently produces `restriction::{ruleId}` — a malformed
   contractual string, discovered late.
3. **Column-per-subject-type, or a discriminator?** `AccessRule` has a nullable
   `PageId` plus a check constraint; `Watch` has a space-or-page XOR with two
   filtered unique indexes. Both are O(n) in columns and constraints per subject
   type. At two types this is fine; **at three it is the moment to switch to
   `(SubjectKind, SubjectId)`** rather than add another nullable column and
   another constraint arm. Adding issues *is* the third type.
4. **One search index or two?** Federated ranking is a real design question, not
   a mechanical one.
5. **Name collision:** `GitLabIssue` already exists in the schema as a
   read-only external type. Pick the internal name deliberately.

## What a v1 should not be

Jira's surface is enormous and most of it is not what makes this product
valuable. Explicitly out:

- **JQL.** A query language is a product in itself. Structured filters first.
- **Dashboards and reporting.** Burndown, velocity, cumulative flow — none of
  it is load-bearing for an export-controlled engineering org's first tracker.
- **Automation rules.** A rules engine is a second workflow engine.
- **SLAs, service desk, request portals.** A different product.
- **Sprints,** unless Scrum is actually wanted. Kanban is a complete v1.
- **Time tracking and worklogs.**

## Risks

**The largest risk is destabilising a compliance-reviewed access-control
system** for a feature unrelated to it. Mitigation: Phase 0 changes no
behaviour and is guarded by the existing suite plus the §21 composition
invariants; every later phase adds a subject rather than altering the engine.

**Audit vocabulary growth is a compliance artifact**, not just a code change.
`AuditSubjectType` gaining members changes what the audit log can say, and the
audit log is the regulated record. It should be a deliberate, documented change.

**Scope is the second-largest risk.** A tracker invites feature requests
indefinitely. The phase boundaries above are chosen so that stopping after any
of them leaves something coherent.

**The unverified-infrastructure caveat still applies.** Per README's status
ledger, nothing has run against real containers yet. Adding a second vertical
does not change that, and does not reduce its importance.
