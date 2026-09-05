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

### The rule engine — one engine, two consumers

Conditions, validators, post-functions and (later) automation rules are the
same thing: a boolean tree over a context, plus a list of effects. Design it
once for both consumers. Retrofitting triggers onto a transition-shaped engine
is expensive; designing for both from the start costs nothing.

**And you already have this engine.** §6.3's ABAC rules are a validated JSON
expression tree with a serializer, a builder UI, and a vocabulary query feeding
it:

```csharp
public abstract record RuleNode;
public sealed record AllOfNode(IReadOnlyList<RuleNode> Children) : RuleNode;
public sealed record AnyOfNode(IReadOnlyList<RuleNode> Children) : RuleNode;
public sealed record GroupCondition(string Group) : RuleNode;
public sealed record UserCondition(string UserId) : RuleNode;
public sealed record AttrCondition(string Attribute, IReadOnlyList<string> In) : RuleNode;
public sealed record EveryoneCondition : RuleNode;
```

`RuleExpressionSerializer` validates holistically — "no notion of a partially
valid tree" — and the whole thing is stored in a column. The workflow engine is
this, extended. Not a parallel design.

**Three node families.** Conditions answer *may this happen*; the actor half
reuses the nodes above verbatim, because the actor **is** a `Principal`:

```json
{ "allOf": [ { "group": "engineering" },
             { "field": "priority", "gte": 3 },
             { "state": { "categoryIs": "InProgress" } } ] }
```

Validators are the same family plus a message
(`{ "when": {...}, "reject": "Set a resolution before closing." }`). Effects are
a separate family — `setField`, `assign`, `addComment`, `addLabel`,
`transitionLinked` — and the vocabulary is closed: nothing that grants access,
changes a marking, or manages principals is expressible (see "Automation" for
why that closure is load-bearing rather than tidy).

**Two deliberate exclusions.**

*No `NOT` combinator*, matching §6.3's "allow-list thinking only". Negation comes
from positive predicates (`isEmpty`/`isNotEmpty`, `equals`/`notEquals`), which
reads better in a builder and avoids the double negations nobody catches in a
config screen.

*No `setMarking` effect.* A rule must never change a protective marking. §21
requires that an author cannot mark above their own clearance and that
downgrades are individually audited as a distinct action; a config screen that
silently reclassifies content defeats both. Classification stays a human
decision.

**The context** is `ctx.Issue.<field>`, `ctx.Actor`, `ctx.Transition.{from,to}`,
`ctx.Now` (resolved once) — built under the acting principal, so a linked issue
the actor cannot see is simply unreachable. The permission model does the work;
the rule engine does not reimplement it.

**Why this beats every scripting engine: there is no engine to escape.** A JSON
AST cannot loop, recurse, allocate unboundedly, or name a type. No sandbox, no
worker process, no resource limits, no `StackOverflowException` killing the pod.
It is also diffable, versionable and auditable — a rule change is a JSON diff in
an audit row, which is what a reviewer wants and what a code blob is not — and
a rule becomes unit-testable by evaluating the tree against a synthetic context.

### If you still want real code

The declarative tier covers the overwhelming majority of real post-functions. If
genuinely arbitrary logic is needed later, the engine choice is not "which
language" but **deny-list versus allow-list**:

- **Roslyn / `CSharpScript`** starts with a language that can do everything and
  tries to remove capabilities. It cannot. .NET has no supported in-process
  sandbox — CAS was removed in .NET Core and `AppDomain` isolation went with it —
  so a script runs with the *process's* authority, not the author's, and
  `Type.GetType("System.IO.File")` defeats any reference allowlist. Three failure
  modes need no malice at all: an uncatchable `StackOverflowException` kills the
  process, `while(true)` cannot be aborted (`Thread.Abort` is gone), and every
  saved script version leaks an assembly into the non-collectible default load
  context. Only defensible out-of-process, with no ambient authority.
- **Jint** starts with a script that can do nothing. Pure managed — no native
  binary, which matters for air-gapped image builds — with CLR interop **off**
  unless explicitly enabled, and real limits the interpreter can enforce because
  it owns the loop: `LimitRecursion`, `LimitMemory`, `TimeoutInterval`,
  `MaxStatements`. Nothing is emitted, so nothing leaks.
- **ClearScript (V8)** if performance or complete modern JS matters; costs a
  native dependency per platform.
- **Dynamic Expresso** for conditions and validators specifically — C#-like
  *expressions* compiled to LINQ trees over registered identifiers only. No
  statements, no loops, so structurally cannot hang.
- **Avoid IronPython and Python.NET**: full CLR access by design, no boundary.

**The context object is the boundary, not the engine.** Hand any of them a
`DbContext` and the sandbox is decorative.

### Automation — the second consumer

Jira Automation is `trigger → condition → action`; workflow post-functions are
`transition → condition → action`. Same conditions, same effects, different
front half.

**Two things it needs already exist.** The trigger source is the domain-event
stream — every mutation raises one, and `DomainEventAuditMapper`'s fall-through
*throws* for an unmapped event type, so the stream is provably complete. And
automation must run **after** commit, not inside the triggering transaction, or a
slow rule blocks the user's save and a failing rule rolls back their edit — which
is exactly the append-in-transaction, drain-after-commit shape the sync outbox
already implements. Second consumer, same pattern.

**RQL is the scheduled-scope language**: "every issue matching
`label = "review" AND updated < now("-30d")`" is a query that exists and is
already permission-filtered.

**DECIDED: automation runs as a defined principal, never as the system.**

A post-function has an easy answer — the user who clicked. A scheduled rule at
03:00 has no such user, and the tempting answer, *the system*, is precisely the
bypass §21 exists to prevent: a rule with process authority could read and relay
content its author cannot see. So each project gets a **named automation
principal**, with its own grants and its own clearance, gated exactly like a
human.

The invariant that makes this safe is simple and worth stating outright: **a rule
can never do more than its principal.** Everything else falls out of it — a rule
triggering on "any issue created" fires only for issues that principal can see;
its actions audit under an identity a reviewer can name; and disabling a runaway
rule is revoking a principal's grants, a mechanism that already exists and is
already audited.

What that decision then requires:

- **It is an execution identity, not a login.** No credentials, no OIDC session,
  no JIT provisioning path — it must be impossible to *sign in as* the automation
  principal and inherit its grants. It is created and configured by an instance
  admin, not by a token arriving at the door.
- **It carries the same attributes a human does** — clearance, and nationality
  for eyes-only caveats — because §21 gates it through the same
  `ClearanceGate`. Set deliberately: the principal's clearance is the ceiling on
  everything every rule in that project can ever touch, which makes it the single
  most useful knob an admin has.
- **Per project, not per instance.** One instance-wide automation identity is
  simpler and strictly more dangerous: its blast radius is every project and its
  clearance is the maximum any project needs.
- **Audit must distinguish a rule from a human.** The actor is the automation
  principal and the row should also carry the rule id, so a reviewer reading the
  log sees *which* rule acted rather than an anonymous service account. The
  existing `AuditChannel` vocabulary is the natural place for an `Automation`
  member.
- **No effect may grant access.** Access-rule mutation, marking changes and
  principal management are excluded from the effect vocabulary entirely —
  otherwise privilege escalation is one post-function away: a rule running as a
  principal that can edit access rules can grant that principal more access, and
  the ceiling above stops being a ceiling.

The rejected alternative was running as the rule's author, re-resolved at
execution time. It tracks the author's clearance nicely, but rules then die
silently when someone changes role or leaves — and "the automation stopped
working because a person moved team" is a failure mode nobody diagnoses quickly.

**Cascades are worse here than for workflows.** Post-functions cascade through
explicit links; automation rules cascade through events they generate
themselves — rule A updates a field, triggering rule B, which re-triggers A.
Needs a depth limit, loop detection, a rule-disables-itself circuit breaker, and
an execution log recording the **causal chain** rather than just the outcome.
Without that log, a rule that silently stops firing is undiagnosable.

**Two things to keep out of v1.** *Outbound HTTP actions* — the most useful
action in Jira Automation, and in an air-gapped or high-side deployment an
egress channel that can carry issue content anywhere; it needs the same
deliberate decision as email, plus a destination allowlist. And *smart-value
templating* beyond simple named substitution: Jira's nested field-path
expressions are a scripting engine wearing a costume, and reintroduce everything
the JSON AST just eliminated.


### Configuration is instance-local

Workflow definitions, issue types and field registries are configuration, not
content — like the ABAC attribute registry and the emoji registry, they do not
travel in sync bundles (§12). A replica materializes what it needs on import.
Worth deciding explicitly, because the alternative (syncing workflow config)
means a high-side instance's process being changed by a low-side push.

## Forms on wiki pages — a ticket-creation front end

This section replaces a separate proposal, `ENTRIES-AND-FORMS-PLAN.md`, which
designed a per-page record store ("entries": JSON objects stored against a
page, each with its own protective marking) and a ConfiForms-style form on top
of it. The store was built, reviewed, and removed (the last tree carrying it is
tagged `full-feature`); what survives of the proposal is re-argued here for its
new home rather than pasted. The decision that reshaped it: **a form on a wiki
page raises an issue in the tracker. It stores nothing of its own.**

### What that decision dissolves

Most of the old document was about storage, and a form that files a ticket has
none. Each point is retired for the reason given rather than silently dropped:

- **The entry table and the collection model.** A submission is an issue (Part
  1, Phase 1). Its container is the project, its identity is the issue key, and
  "every record this form ever produced" is an RQL query over issues — the
  cross-page query the old plan kept out of v1 *because* entries were
  page-scoped. No new table, no `(PageId, Collection)` index, no size cap or
  per-page quota of its own, no collation trap to test on two providers.
- **Per-entry markings, and everything that followed from them.** An issue
  carries a marking as every tracked item does ("Markings apply per issue",
  below). So the old Part 2 — a second gate on every read path, the
  pruned-entry-indistinguishable-from-absent work, keyed cursors over a
  permission-filtered record list, a per-viewer aggregate banner over a page's
  records, and the question of an author locked out of their own submission —
  is the tracker's existing problem, solved once for issues rather than a
  second time for records on a page.
- **Entries in search, RAG and sync.** Issues are indexed, retrieved and synced
  by the tracker (Phases 4 and 5). There is no second content type to keep out
  of retrieval "for now" and no second sync event type to design. (The retired
  store's `SyncEventType.PageEntry`, wire value 11, stays reserved and is never
  reissued — design.md §12.)
- **The `form-list` display fence.** A table of records rendered into a page is
  a saved RQL filter over issues, which Part 2 needs anyway for queues. Do not
  build a second listing widget with its own filter grammar.

### What survives, reframed

**The form definition is a fence, not a table.** The argument has not changed
and never depended on where submissions went:

~~~
```form
project = FACILITIES
issueType = incident-report
field = severity: select(low, medium, high), required
field = summary: text, required
field = occurredAt: date
```
~~~

It round-trips as text, so the definition is versioned by the page's own
revision history, diffable and restorable for free; it needs no schema
migration when the field vocabulary grows; and a reserved fence that stays a
plain `codeBlock` touches nothing in the Markdown round-trip, sync bundles
(§12) or the importer (§13) — the property every other widget fence already
has (§4, §18, §22). The trade-off is the same too: the server cannot read a
definition without parsing page content, so the parser lives in Core and is
the one grammar — the SPA renders what the server parsed and never re-parses,
exactly as it does not re-parse RQL.

**Field types.** `text | number | date | select` were the old four, chosen
because each is a validation rule, an input control and a sort order. They are
now a *subset* of the issue field types the site admin defines (`text | number
| date | user | select | multiselect | checkbox`, "Issue types and their
attributes" above): the form declares which issue attributes it collects and
how, it does not invent types the issue cannot hold. A field the form names
that the issue type does not have is a definition error, reported to the author
with the line, exactly as a malformed field was.

**Display.** The fence renders as the form to fill in. A viewer without the
right to create an issue in the target project sees the definition and no
submit control — not a button that fails afterwards. Definition errors are
shown to the author (which form, which line, what was wrong) rather than making
the form silently vanish, which was the old parser's rule and is kept.

**What a v1 should not be** — carried over, and the first line now matters
more than it did:

- **No workflow in the form.** ConfiForms has state transitions and actions;
  here they belong to the tracker's workflow engine, applied to the issue the
  form raised. The form is the *creation* step and nothing after it. This was
  the first line of the old list when the engine was hypothetical; now that the
  tracker has real workflow, the temptation to let a form "close the ticket
  too" is a second engine one field away.
- **No email or webhook on submit.** Automation runs as a defined principal
  (decided above), and a form is not one.
- **No file-upload fields.** Attachments go onto the issue afterwards, through
  the attachment path with its own storage, quota and streaming story.
- **No per-field markings.** The issue carries one marking.
- **No submission store of any kind** — not "for a draft", not "in case the
  tracker is down". A form that cannot raise an issue says so and keeps the
  typed values in the browser until it can.

### Where it sits in the phasing

After Phase 2 and no earlier (listed there as Phase 2b): it maps fields onto
*typed* issue attributes, which Phase 1's primitive issue does not have. A
request type in Part 2 is an issue type plus one of these forms, so Phase 6's
portal reuses this definition format rather than growing a second one.

### Decide before writing code

1. **Which project, and who chooses it?** The fence names a project (a Space
   of kind `Tracker`, per the pivotal decision), so the *author* decides where
   submissions go and the submitter needs create rights there. A form that let
   the submitter pick would be a project browser with a form attached.
2. **Which issue type, and how strictly?** Named in the fence. May a form omit
   a field the issue type marks `required`, and let the issue land incomplete?
   Recommendation: no — the form must cover every required field, checked when
   the definition is parsed so the author finds out, not the submitter.
3. **What does a field map to?** An issue attribute, by name. Free text that is
   not an attribute — a description — maps to the issue's Markdown description.
   Whether several fields may be *composed* into the description (a template)
   is scope creep toward smart-value templating, which Part 1 keeps out of v1.
4. **What does the submitter see afterwards?** The issue key and a link, if
   they can view the issue. If they cannot — a form can legitimately file into
   a project the submitter cannot read; one-way reporting is a real use — a
   confirmation that says nothing about the issue, not even its key. §6.7
   applies: "you may not see what you just created" and "it was created, here
   it is" must differ by nothing but the presence of the link.
5. **Which marking does the issue get?** The project's default, as for any issue
   created in it — never derived from the page the form sits on. A form on an
   OFFICIAL page can collect a SECRET report, and the reviewer of that report is
   the tracker's, not the page's. Whether a submitter may set a marking they
   could not then read follows the tracker's rule for issues, whatever it is.
6. **Audit.** A submission is `issue.create` on the tracker's channel, with the
   page id in the details so a reviewer can see which form filed it. No
   separate form audit action.
7. **The fence name.** `form-definition` was the retired feature's reserved
   language and is reserved nowhere now. Reusing it is fine, but a page from a
   `full-feature` tree still carries the OLD grammar (`collection = ...`, no
   project), and that must parse as a definition error naming the missing
   project — never as a form aimed at nothing.

### Risks

- **A second definition format.** If Part 2's request types grow their own
  form schema, there are two ways to describe one form. The fence grammar
  should *be* the request type's grammar, or one a projection of the other,
  decided before either ships.
- **Templating.** Composing fields into a description is where a form stops
  being a form and starts being a scripting surface.

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
execution model (see "If you still want real code").

**Phase 2b — forms on wiki pages.**
The ticket-creation front end described in "Forms on wiki pages": a reserved
fence whose submission raises an issue with typed attributes. Needs Phase 2's
typed fields and nothing from Phase 3. Cheap because it stores nothing — the
page holds the definition and the tracker holds the result.

**Phase 3 — links and boards.**
Issue links with the permission-filtered rendering described above. Kanban board
over workflow states. Aggregate markings on columns.

**Phase 3b — automation.**
The rule engine from Phase 2 gains a trigger layer: subscribe to the
domain-event stream, drain after commit through the outbox pattern, add
scheduled rules scoped by RQL. Runs as the project's named automation principal
(decided — see "Automation"), which also means building the admin surface to
create one and set its clearance. Needs cascade limits and an execution log
recording the causal chain. Cheap *only* because the conditions and effects already exist —
which is why the rule engine should be designed for both consumers in Phase 2
rather than shaped around transitions.

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
3. **Is the JSON rule AST enough, or is real code required?** The AST needs no
   sandbox because there is no engine to escape, and it covers the overwhelming
   majority of real conditions and post-functions. If arbitrary logic is genuinely
   required later, the choice is an allow-list engine (Jint, in-process, CLR
   interop off) or out-of-process C# — never in-process Roslyn, which repeals
   §21's "no role reads around a classification" by running with the process's
   authority rather than the author's. A security decision, not a feature one:
   deferring it is fine, deferring it *while shipping in-process scripting* is
   not.
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
- **Outbound HTTP from automation, and smart-value templating.** The rule
  engine itself is planned (see "Automation"), but its two most dangerous
  features are not: web-request actions are an egress channel for issue content,
  and nested field-path templating is a scripting engine wearing a costume.
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
| Request type | An issue type plus a form definition — the wiki-page form of Part 1's "Forms on wiki pages", not a second format |
| Queue | A saved RQL filter with an ordering |
| Agent / customer split | Container roles — `agent` is a role, **never a bypass** |
| Ticket conversation | Comments, already threaded with tombstones |
| Attachments, labels, watches, notifications | Unchanged |
| Deflection | Existing permission-filtered search + Ask |
| Approvals | A workflow state plus a permission verb |

Genuinely new: the **portal** (a deliberately reduced UI for requesters),
**SLAs**, **CSAT**, and **canned responses**. Form definitions are designed once,
in Part 1's "Forms on wiki pages"; a request type reuses that grammar.

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
