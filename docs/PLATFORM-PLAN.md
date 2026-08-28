# Platform plan: issue tracking and service desk

**Status: a proposal, not a decision.** Nothing here is built. This document
exists to make the shape of the work visible before anyone commits to it, and
to record the handful of choices that are expensive to change later. It is
written in the same voice as the rest of the repo: what is genuinely known,
what is a judgement call, and what nobody has decided yet.

The question: *what would it take to turn RocketWiki into a platform that also
replaces Jira — and then Jira Service Management?*

Two parts, in dependency order. **Part 1 (issue tracking)** is the foundation
and stands alone. **Part 2 (service desk)** is a front door onto it and cannot
start before Part 1's Phase 3. Each part ends with its own decisions and risks.

## Part 1 — Issue tracking: the short answer

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

## Admin configuration, at the site level

Everything below is configured by instance admins through the UI, stored as
data, and versioned. This is the shape Jira proved and there is little reason
to deviate from it.

### Issue types and their attributes

An **issue type** is a name, an icon, a hierarchy level, and an ordered set of
**field definitions**. The §20 property registry gets you most of the way — it
is already an admin-governed, instance-level registry with a normalized-key
unique index — but it needs two extensions:

- **A type on the key.** Properties are plain text today. Fields need
  `text | number | date | user | select | multiselect | checkbox`, because
  sorting, validation and RQL comparisons all depend on it. This is the same
  shape `AttributeDefinition` already has for the ABAC registry (`Type`,
  `AllowedValuesJson`) — copy that, do not invent a third registry.
- **Assignment to an issue type**, with per-assignment `required` and a default.

Deleting a field definition in use must be **refused with the usage count**,
exactly as deleting an in-use property key is (§20). Changing a field's *type*
after data exists is the migration nobody plans for: either forbid it, or make
it an explicit convert-with-preview operation. Forbidding is defensible for v1.

### States and status categories

A **state** is a name, a colour, and exactly one **status category** from a
fixed three: `To Do`, `In Progress`, `Done`. The categories are deliberately
not admin-editable — boards, "is this resolved", cycle-time and every future
report key off them, and letting an admin invent a fourth breaks all of it.
This is precisely Jira's model and its rigidity is the feature.

`Done` is the one with semantics: it is what closes an issue, stops SLA clocks
(Part 2), and removes a card from a board's active columns.

### Hierarchy

Levels are defined at site level as an ordered list — e.g. `Epic (2)`,
`Story (1)`, `Sub-task (0)` — and each issue type sits at exactly one level. The
rule is simply that a parent must be at a strictly higher level than its child.

Note this is **typed hierarchy**, unlike the page tree: a page may parent any
page, but an Epic may only parent types at a lower level. That is a domain
constraint, not a permission one — the permission engine already handles a flat
subject and a chain identically, so hierarchy costs nothing there.

Cycle prevention is the one non-obvious requirement: reparenting must reject a
move that would make an issue its own ancestor. The page tree solves this with
`AncestorPath`; issues can do the same or walk parents on write.

### Basic workflows

A **workflow** is a set of states plus transitions between them, assigned to an
issue type (per project, or site-wide with per-project override — pick one;
site-wide-with-override is what Jira does and what people expect).

A transition is: `from → to`, a name, and three hook points that Jira's model
has proven and which are worth copying by name because they are genuinely
distinct concerns:

| Hook | Question it answers | Runs when |
|---|---|---|
| **Condition** | May this actor even see/attempt this transition? | Before the button is offered |
| **Validator** | Is the submitted input acceptable? | On submit, before any change |
| **Post-function** | What else happens as a result? | After the state change, same transaction |

Declarative versions of all three cover the overwhelming majority of real use:
condition on role/field value, validator on required-fields/regex, post-function
to set a field, assign, add a comment, or transition a linked issue.

**Two rules specific to this product:**

1. **Transitions need their own permission verb.** `PageAction` has exactly two
   members (`View`, `Edit`); a tracker needs at least `Transition` and `Assign`.
   Adding members is additive — the values are tinyint and stored values do not
   move — but the *name* `PageAction` leaks into denial reasons and GraphQL, so
   rename it when the seam is extracted (Phase 0).
2. **A post-function runs as the acting user, never elevated.** If a
   post-function sets a field on a linked issue the actor cannot see, it must
   fail — visibly — rather than succeed. Otherwise workflows become a permission
   bypass, and the §21 invariant that no role reads around a classification is
   defeated by a config screen. Audit rows for post-function effects carry the
   acting user, not a system account, or §7's record stops being true.

### Advanced workflows: admin-authored C#

This is the request that needs its constraints stated before anyone starts,
because the obvious implementation is unsafe in a way that is not obvious.

**The hard fact: .NET has no supported in-process sandbox.** Code Access
Security was removed in .NET Core, and `AppDomain` isolation went with it. There
is no mechanism by which C# compiled and executed inside the API process can be
prevented from opening a `DbContext`, reading any row, calling out over HTTP, or
reading the process's configuration and secrets. Roslyn scripting compiles and
runs code; it does not contain it.

For this product that has a specific consequence. §21 establishes that **no
role — including instance admin — reads around a classification**; the clearance
gate has no admin bypass, and that invariant is enforced structurally and pinned
by test. Admin-authored C# running in-process silently repeals it: the script
runs with the process's authority, not the author's. An instance admin who
cannot read a `TOP SECRET` page could write a post-function that emails its
contents outside. Nothing in the current design would notice.

So the real decision is not *"C# or not"* but *"what trust boundary"*. Three
honest options:

**(a) Declarative rules, no code.** A safe expression language over a curated
context — field reads, comparisons, and a fixed set of effects. Covers the great
majority of real post-functions, is statically analysable, has no sandbox
problem, and can be validated in the UI as it is typed. *Recommended for the
first advanced tier.*

**(b) A developer-shipped catalogue.** Post-functions are real C#, written by
developers, reviewed, and deployed with the application; admins compose and
configure them through the UI but cannot author new ones. This is how most
regulated deployments actually run ScriptRunner-style features once the security
team looks at them. It gets you arbitrary power without an arbitrary-code
surface.

**(c) UI-authored C#, executed out-of-process.** If admins genuinely must write
code in the browser, the only defensible execution model is an isolated worker
with **no ambient authority**: a separate process or container, no database
connection, no network egress, no secrets, communicating over an explicit
`IWorkflowContext` boundary that exposes only what the acting user could do
anyway. Add resource limits (CPU, memory, wall-clock), a hard timeout, and treat
script changes as deployment-class events — versioned, diffable, audited, and
ideally requiring a second approver.

Even in (c), be honest in the docs that **authoring a script is equivalent in
power to deploying code**, and that the boundary is the worker process, not the
language.

If (c) is the destination, (a) is still the right first step: it is a subset of
the same UI, it defines the context object the sandbox would later expose, and
it lets the workflow engine ship without waiting on the isolation work.

### Configuration is instance-local

Workflow definitions, issue types and field registries are configuration, not
content — like the ABAC attribute registry and the emoji registry, they do not
travel in sync bundles (§12). A replica materializes what it needs on import.
Worth deciding explicitly, because the alternative (syncing workflow config)
means a high-side instance's process being changed by a low-side push.

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
Admin configuration first: issue types with typed fields, states bound to the
three fixed status categories, hierarchy levels, then transitions with
declarative conditions/validators/post-functions. New permission verbs
(`Transition`, `Assign`). Transition history in the audit log — a state change
is exactly the kind of thing §7 exists to record. Scripted post-functions are
**not** part of this phase: ship the declarative tier, then decide the
execution model (see "Advanced workflows").

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
3. **What trust boundary for scripted workflows?** Declarative rules, a
   developer-shipped catalogue, or UI-authored C# in an isolated worker. The
   answer determines whether §21's "no role reads around a classification"
   survives contact with the workflow engine, so it is a security decision, not
   a feature decision. Deferring it is fine; deferring it *while shipping
   in-process scripting* is not.
4. **Column-per-subject-type, or a discriminator?** `AccessRule` has a nullable
   `PageId` plus a check constraint; `Watch` has a space-or-page XOR with two
   filtered unique indexes. Both are O(n) in columns and constraints per subject
   type. At two types this is fine; **at three it is the moment to switch to
   `(SubjectKind, SubjectId)`** rather than add another nullable column and
   another constraint arm. Adding issues *is* the third type.
5. **One search index or two?** Federated ranking is a real design question, not
   a mechanical one.
6. **Name collision:** `GitLabIssue` already exists in the schema as a
   read-only external type. Pick the internal name deliberately.

## What a v1 should not be

Jira's surface is enormous and most of it is not what makes this product
valuable. Explicitly out:

- **JQL.** A query language is a product in itself. Structured filters first.
- **Dashboards and reporting.** Burndown, velocity, cumulative flow — none of
  it is load-bearing for an export-controlled engineering org's first tracker.
- **Automation rules.** A rules engine is a second workflow engine.
- **SLAs, service desk, request portals** — out of *tracker* v1, but planned
  rather than rejected: see "Service desk" below. It sits on top of the
  tracker and should not be attempted before Phase 3 exists.
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

---

# Part 2 — Service desk

A Jira Service Management alternative — request portal, queues, SLAs,
approvals. **Phase 6 and beyond**: it is a front door onto the tracker, so it
cannot start before Phases 1–3 exist. What follows is why it is a better fit
here than it looks, and where it is considerably more dangerous.

## Why this one is worth doing

The strategic argument is not "Jira has it too". It is that **Atlassian sells
Confluence and JSM together for a reason, and here they are one product**.

A service desk's most valuable feature is *deflection*: answering the request
before a ticket exists. That needs a knowledge base, permission-filtered
search over it, and ideally a retrieval-augmented assistant. RocketWiki already
has all three, and Ask-the-wiki already retrieves strictly under the caller's
own principal with an aggregate protective marking on the answer (§21.13). A
portal that says *"three existing articles may answer this"* before offering a
form is, here, a query away — not an integration.

Second: **queues are RQL filters**. Saved filters were deliberately deferred
out of RQL v1 (§22) on the grounds that a saved filter is a new securable
object whose *definition* can itself be sensitive. A service desk is the thing
that makes them necessary, and it is the right moment to design them properly
rather than early.

## What reuses what

| Service desk concept | What it is here |
|---|---|
| Request type | An issue type plus a form definition |
| Queue | A saved RQL filter with an ordering |
| Agent / customer split | Container roles — `agent` is a role, **never a bypass** |
| Ticket conversation | Comments, already threaded with tombstones |
| Attachments, labels, watches, notifications | Unchanged |
| Deflection | Existing permission-filtered search + Ask |
| Approvals | A workflow state plus a permission verb |

Genuinely new: the **portal** (a deliberately reduced UI for requesters), **form
definitions**, **SLAs**, **CSAT**, and **canned responses**.

**SLAs are the one substantial new subsystem** — not the timer, which is easy,
but working calendars (business hours, holidays, timezones), pause conditions
("waiting for customer"), breach escalation, and the fact that changing an SLA
definition must not retroactively rewrite history. Budget for it accordingly;
everything else on that list is small.

## Where it gets dangerous here

**1. Who is allowed to raise a request?** This is the decision the whole design
hangs on, and it must be made first.

The product currently has *no anonymous access at all* — §6.1's "no anonymous
wikis" — and every principal is a Keycloak subject carrying ABAC attributes
including nationality and clearance. A service desk's classic requester is an
*external* party with none of that.

**Recommendation: internal requesters only.** IT, facilities, engineering
support — people who already have principals. If external requesters are ever
needed, the right shape is a **separate low-side instance** whose tickets sync
upward through the existing one-way bundle mechanism (§12), not a portal
punched into a high-side deployment. That preserves the boundary the entire
architecture is built around, and it is a far smaller change than making the
main instance safe for outsiders.

**2. "Agent sees the whole queue" is an ABAC hole.** Every other product treats
agent visibility as a given. Here it cannot be: a queue that shows everything
would let an agent read whatever anyone pasted into a ticket, and clearance
would be bypassed by the front door. Agents need grants like everyone else, and
the queue is a permission-filtered listing — which also means **queue counts and
SLA dashboards are §6.7 surfaces**: a "47 open" badge over 31 visible tickets
leaks 16.

**3. Tickets accumulate classified content by accident.** A user pastes a SECRET
extract into a support request that was raised as OFFICIAL. Markings apply to
tickets exactly as to pages, defaulting from the request type — and the lesson
from §21.11's backfill applies directly: **an unreviewed default marking reads
as a reviewed judgement**. A request type's default marking should be chosen as
carefully as a page's.

**4. Deflection must run as the requester.** The article suggestions and any
assistant answer must retrieve under the requester's own principal. Running them
as a service account — the obvious implementation, and how many products do it —
would turn the portal's helpful-suggestions box into a disclosure channel for
titles the requester cannot otherwise see.

**5. Notifications are an egress question.** A service desk without requester
notifications is barely a service desk, and the product currently sends no email
at all. In an air-gapped or high-side deployment, outbound mail is a boundary
crossing that needs a deliberate decision — including what may appear in a
subject line, since a ticket title can carry the sensitive fact.

**6. One-way sync cuts both ways.** If a requester is on the low side and agents
are on the high side, the response cannot flow back: §12 is low → high, by
design, permanently. A cross-domain support workflow is therefore *not*
expressible in this architecture, and pretending otherwise would be the most
expensive possible mistake. Say it out loud before anyone assumes it.

## Phasing

- **Phase 6 — requests.** Request types with forms, the portal as a reduced
  view, internal requesters only. Queues as saved RQL filters, which forces the
  saved-filter design.
- **Phase 7 — SLAs.** Calendars, pause conditions, breach escalation, and the
  no-retroactive-rewrite rule.
- **Phase 8 — approvals and CSAT.**
- **Deflection lands in Phase 6**, not later. It is the cheapest thing on the
  list and the reason the product is differentiated; shipping the portal without
  it would be shipping the least interesting half.

## Decide before starting

1. **Internal requesters only, or external?** Everything above assumes internal.
2. **Is there email?** If not, requesters are notified in-app only, and that
   constrains what "service desk" can mean here.
3. **Do agents get a broad grant, or per-project grants like everyone else?**
   The honest answer is the second; the convenient answer is the first.
