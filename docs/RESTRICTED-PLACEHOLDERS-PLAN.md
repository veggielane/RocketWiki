# Plan: restricted-page placeholders

**Status: a plan, not a decision, and nothing is built.** This describes how an
opt-in setting would work that shows *"(protected)"* where a page the caller
cannot read would otherwise be absent.

It is written down before implementation because the feature **deliberately
weakens a property the rest of the system is built on**, and that is worth
recording in one place rather than discovering across a diff.

## What this changes, and why that is significant

design.md §6.7 guarantees that a page the caller cannot read is
**indistinguishable from one that does not exist** — byte-identical at the HTTP
boundary, absent from listings, and never implied by a count. That rule is why
search totals, page-tree gaps, label listings and link lists all *omit* rather
than *redact*, and it is pinned by
`DeniedAndMissing_PageResponses_AreByteIdentical_AtTheHttpBoundary`.

This feature converts "invisible" into "existence disclosed" for one class of
denial. That is a legitimate product choice — other wikis work this way, and
navigational honesty has real value — but in an export-control deployment the
*existence* of a page can itself be the sensitive fact. A `(protected)` node
under `/Programs/<name>/` tells a reader that the programme exists, how many
documents it has, and roughly where they sit.

## Decisions taken

| Question | Decision |
|---|---|
| Which denials get a placeholder? | **Restrictions only.** Clearance and caveat denials stay completely invisible. |
| How wide is the setting? | **Instance-wide, off by default.** |
| Which surfaces? | **Page tree and direct links only.** |

The first is the security-critical one. A restriction answers *"who may see this
particular thing"*; a classification answers *"what sensitive material exists"*.
A placeholder at a known tree position tells an uncleared reader that classified
material exists at that location — which is the same census problem §21.8
already refuses for telemetry, where even a bounded four-value level is kept out
of metric dimensions because a denials-by-level series is a map of the
classified estate.

## 1. The setting

`Access:ShowRestrictedPlaceholders` — bool, default `false`, in the config
family alongside the other fail-closed keys.

**Config, not a database row with an admin toggle.** This changes what the
deployment discloses, so it should require the same rigour as changing a
connection string: visible in the Helm values, reviewable in a diff, not
flippable in a browser by whoever currently holds the admin role. It also means
there is exactly one answer to *"does this instance disclose existence?"*, which
is the question an auditor asks.

Needs:
- a row in `docs/CONFIGURATION.md`, marked as a **weakening** — every key
  documented there so far fails *closed*; this is the first that opens something
  up, and the table should say so;
- a §6.7 amendment in design.md describing the setting, its default, and its
  exact scope;
- **one startup log line when it is ON.** §15's posture is "log almost nothing",
  and this is the exception that earns its place: an operator inheriting a
  deployment should be able to see this is enabled without reading config.

## 2. Distinguishing "restricted" from "over-classified"

The detail that makes the chosen split implementable: denial reasons are already
a structured vocabulary.

```
restriction:{pageId}:{ruleId}   ← placeholder eligible
classification:{level}          ← stays invisible, always
caveat:eyes_only                ← stays invisible, always
```

`ReadResult` already carries *Denied-with-reason* separately from *NotFound* —
that distinction exists for the audit log, and this feature reuses it rather
than inventing a parallel mechanism.

**Fail-closed rule:** emit a placeholder only when the reason *positively
matches* the restriction form. Any unrecognised reason omits. A denial category
added later then defaults to invisible instead of silently disclosing.

## 3. What a placeholder contains

The title `(protected)` and nothing else. Specifically **no** id, timestamps,
author, labels, marking, child count, or any position beyond where it already
sits in the tree.

Suppressing the id matters more than it appears: page ids are UUIDv7, so an id
discloses its own creation time to the millisecond. A placeholder carrying a
real id would leak *when* the hidden page was created. Placeholders are not
navigable, so they do not need one.

**A placeholder is a leaf.** Its subtree stays pruned even where individual
descendants would be viewable. That is today's behaviour and preserving it
avoids leaking structure *below* a hidden node. Known cost, stated rather than
discovered: a viewable page beneath a restricted parent stays unreachable by
browsing, though it remains reachable by id and through search.

## 4. Where the change goes

This is a **presentation** decision derived from an access decision. `canView`
does not move.

- **`PageReadService.GetPageTreeAsync`** — the tree walk hand-rolls its own
  restriction walk (a consistency review flagged it as a straggler from the
  permission-loader consolidation). One branch: where it currently skips a
  denied node, emit a placeholder instead when the setting is on *and* the
  reason is a restriction.
- **Link resolution** — a `page://{id}` link mark to an unreadable page renders
  as non-clickable `(protected)` text. No new disclosure: anyone who can read
  the containing page already has that id in its Markdown source.
- **Nothing else.** Search, label listings, the page-list widget, Ask citations
  and MCP keep omitting entirely.

## 5. What must not change

- **`Query.page(id:)` still returns null.** The placeholder is a *listing*
  affordance, not a read. `DeniedAndMissing_PageResponses_AreByteIdentical_AtTheHttpBoundary`
  must pass with the setting in **both** states, and should be run in both.
- **No count changes.** A tree showing placeholders is showing them, not
  counting them; nothing gains a "12 hidden" badge.
- **Clearance denials are unaffected on every surface.**

## 6. API and UI shape

`PageTreeNode` gains `isRestrictedPlaceholder: Boolean!`, with `title` carrying
`(protected)` from the server so every consumer renders identically rather than
each inventing the string.

The flag — rather than string-matching the title — is what lets the SPA style it:
muted, lock icon, not a link, no context menu, and excluded from keyboard
navigation as a non-interactive node.

## 7. Test matrix

- Setting **off** ⇒ today's behaviour, byte-identical; every existing test green.
- Setting **on** ⇒ a restriction-denied page appears as `(protected)`.
- Setting **on** ⇒ a clearance-denied page is **still absent** — tested for both
  a level denial and a caveat denial.
- A page denied by **both** a restriction and clearance ⇒ absent, not a
  placeholder. *This is the case a naive implementation gets wrong.*
- A placeholder carries no id and no timestamps.
- A placeholder is a leaf; a viewable grandchild is not disclosed.
- Direct fetch by id still denied and byte-identical, in both settings.
- Search, widgets and label listings unchanged in both settings.

## 8. Open questions, flagged rather than decided

**MCP `get_page_tree`.** It is also a page tree, but its consumer is usually a
model that may summarise or relay what it sees — a different decision from
disclosing to a person looking at a sidebar. Inclination: exclude MCP, keep it
omitting. Same shape as the open question about whether MCP payloads should
carry classifications.

**Audit volume.** Today a pruned node generates no per-page denial row; the
browse is audited once as `space.browse`. Placeholders should not change that,
or a tree with fifty restricted pages writes fifty rows per browse. The
consequence is that the audit log will not record *which* placeholders a user
saw — acceptable, consistent with current pruning, but worth stating.

## Effort

Roughly a day. The backend change is small and the SPA change is small; the test
matrix above is most of the work — which is the right proportion for a feature
whose entire purpose is to weaken a security property in a controlled way.
