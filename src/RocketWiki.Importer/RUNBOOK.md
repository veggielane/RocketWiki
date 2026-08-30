# Trial import runbook

Companion to design.md §13 (Confluence migration) and §16 ("trial imports against a real
space export" as the way migration fidelity risk gets budgeted, not guessed at). This
tool has never been run against a real Confluence export — everything in it is built from
the documented, long-stable shape of Confluence's storage format and XML export, not from
a captured sample (see `ConfluenceStorageConverter`'s and `ConfluenceXmlExportReader`'s own
remarks). That gap is exactly what a trial import closes. This runbook is what to actually
do, and — more valuable than the mechanics — the specific list of assumptions a real
export will confirm or correct.

## 1. Always dry-run first

```
RocketWiki.Importer --export <path-to-export.zip> --space-key <KEY> --dry-run --report trial-report.txt
```

This reads the export, builds the page tree, resolves every `page://`/`attachment://`
reference, converts every page body and comment, and previews every label — **without
creating anything**, no database required. It exercises the identical tree-planning,
two-pass id resolution, and conversion logic a real import uses (`ImportTreePlanner`,
`TwoPassPageIdResolver`, `ConfluenceStorageConverter` are shared code, not parallel
implementations — see `ConfluenceImportValidator`'s remarks). If the dry run's export
reading itself fails, nothing about your Confluence content is at fault yet — see §5
below before assuming the export is bad.

Do not skip straight to a real run. The two things a dry run *cannot* fully predict —
whether a label name collides with one already in RocketWiki, and whether the real
`RocketWiki.Data` services accept content this converter judged valid — are exactly the
kind of thing worth discovering on the first, disposable trial space, not on the space
that matters.

## 1a. Handling the report file

The report is not a summary — it reproduces **page content and author email addresses
in plaintext**, and on a dry run that means every page of the space. It is written to
whatever path `--report` names, with no protective marking of its own and nothing
restricting who can read it.

Treat it as at least as sensitive as the space it describes: keep it inside the same
boundary, do not paste it into a ticket, and delete it once the migration review is
finished. The report itself now carries this warning in its header.

## 2. Exit codes

| Code | Meaning | What to do |
|---|---|---|
| 0 | Clean — nothing in the report needs a human | Proceed |
| 1 | Usage or input error (bad arguments, export file missing/unreadable, wrong `--space-key`) | Fix the command line or the export path; nothing was attempted |
| 2 | Blocked (space creation failed, or the run stopped part-way on something unexpected) | Read the "Import blocked" message on stdout **and the report, which is still written** — it tells you which of the two happened and, if the run got part-way, exactly what is already in the database |
| 3 | Completed, but the report has something worth reading | **Read the report.** This is the common, expected outcome for a real Confluence space — see §3 |

Exit code 3 is not a failure. A real migration will produce it almost every time; that's
the whole point of the report existing.

**A report is now written for every outcome except a usage error**, exit code 2
included. There is no transaction spanning an import — each service commits as it
goes — so if a run does stop part-way, the report is the only record of what was
already written, and it used to be the one thing you didn't get.

## 3. What to check in the report, in order of how likely it is to matter

1. **Pipeline notes** (top of the report). Anything here is space-wide, not page-specific
   — read it before anything else. Historically this has been the space-admin bootstrap
   note (design.md §6.5.1 — should no longer appear once that fix has fully landed
   everywhere this runs; if it does appear, page creation below will also be failing, and
   that's the reason).
2. **Summary counts.** Pages/comments skipped, pages with no converted content, and label
   failures are structural problems — something didn't get created at all, not just
   converted imperfectly. Chase these to zero before worrying about anything else.
3. **Unsupported macros, by name and count.** If the same macro name appears dozens of
   times, that is worth a deliberate decision (extend the converter, or accept the loss)
   rather than reviewing each occurrence individually.
4. **Unresolvable links.** Each one names the target page/attachment it couldn't find —
   usually means that target wasn't part of this export, or was migrated in a different
   batch. Cross-space links will always show up here; that's expected, not a bug.
5. **Individual "pages needing review" entries**, read for real. This is where lossy
   conversions (underline, panel titles, remapped `tip` macros, table merges split at the
   header/body boundary — merged cells, column alignment, and in-cell line breaks now
   convert faithfully and no longer appear here; see design.md §4 and the converter's own
   report categories) and skipped pages/comments with
   their specific reasons live. The dry run includes a converted-Markdown preview for
   exactly this reason — you're meant to read what the page will actually look like, not
   just that something about it is imperfect.
6. **Original author lines.** Every page/comment not authored by whoever ran the import
   shows its real Confluence author, with a note that it hasn't been applied anywhere
   (there's no shadow-user service yet — design.md §12/§13, tracked with the Core owner).
   This is expected on every real migration until that service exists; it is not something
   this tool got wrong.

## 4. Then, and only then, a real run

```
RocketWiki.Importer --export <path> --space-key <KEY> \
  --connection-string "<sql-server-connection-string>" \
  --attachments-root <directory> \
  --acting-user-id <existing-rocketwiki-user-guid> \
  --importer-principal-id <sub-claim-value-for-the-importer> \
  --grant-role <editor|space-admin> \
  --grant-expression '<access-rule-expression-json>' \
  --report import-report.txt
```

`--grant-role`/`--grant-expression` decide who can see and edit the space once it exists
— **and, for the duration of the run, what the importer itself may write.** Every
service call is made as the importer principal (group `confluence-importer`, no
attributes), so a grant that principal does not satisfy, or a `viewer` role, would
create the space and then refuse every page while still reporting success. Both are
now refused up front, before the space key is consumed, naming the groups and
attributes the principal actually has. If you want an attribute-based grant, run the
import as a principal carrying that attribute — do not widen the grant to get past it.
There is no default — Confluence's own space/page permissions are **not** translated by
this tool (an automatic mapping is guaranteed wrong in one direction or the other: too
open, which leaks content that was export-controlled under Confluence's model, or too
restricted, which reads as data loss). Decide this deliberately for each space; don't
default it to "everyone" out of convenience.

The other half of that bargain is that the report tells you what Confluence had.
Every space permission and page restriction in the export is read and printed
verbatim under **`== Confluence permissions found in the export ==`**, including
permission types this tool has never heard of, and a non-zero count there forces the
report's overall "needs review" flag even when every page converted perfectly.
**Work through that section before you tell anyone the space is ready** — until you
do, a page that five people could read in Confluence is readable by everyone your
`--grant-expression` matches. An empty section is printed explicitly ("the export
carried no space permissions and no page restrictions") so that "there were none"
and "nobody looked" never read the same.

**The real-run database wiring has not been executed end-to-end.** It's built to the same
`RocketWikiDbContext`/service-construction pattern as `RocketWiki.Api/Program.cs`, but no
SQL Server was available while building this tool. The first real run against a real
database is validating that wiring, not just your Confluence content — budget time for
both.

## 5. Assumptions a real export needs to confirm or correct

Everything below is a specific, falsifiable guess about Confluence's XML export format
(`ConfluenceXmlExportReader`), made without a real sample to check against. If a real
export behaves differently, these are exactly the places to fix — not signs the whole
approach is wrong.

- **`entities.xml` object shape.** Assumed: `<object class="X"><id name="id">N</id>
  <property name="Y">...</property></object>`, with a `<property>` containing either a
  `<![CDATA[...]]>`/plain text value, a nested `<id>` (a reference to another object), or
  a `<collection>` of `<element>`s (each with its own `<id>`). This generic shape is the
  most stable part of the format historically — if anything here is wrong, most other
  assumptions below fail too, so check this first.
- **Property names on `Page`**: `title`, `parent` (reference to the parent Page),
  `bodyContents` (collection of `BodyContent` references), `creator` (reference to a user),
  `creationDate`, `contentStatus` (`"current"` vs. `"draft"`/other — only `"current"`
  pages are imported), `attachments`, `space`.
- **`BodyContent.bodyType`**: assumed `"2"` means storage format (what
  `ConfluenceStorageConverter` understands). If a real export never sets this, or uses a
  different value, `ResolveStorageBody`'s fallback (first `BodyContent` found) is what
  actually runs — verify the resulting Markdown looks right regardless.
- **Attachment binary layout**: assumed `attachments/{attachmentId}/{anything}` inside the
  zip, matched by prefix. Older Confluence versions have used
  `attachments/{pageId}/{attachmentId}/{version}` instead — if attachments come back
  empty in the dry run's page list despite `Attachment` objects existing in `entities.xml`,
  this is the first thing to check.
- **Comment ownership and threading** (`ResolveAllComments` in `ConfluenceXmlExportReader`)
  — the least certain part of this whole reader. Assumed: a `Comment` object carries an
  `owner` property (falling back to `content`, then `page`) referencing the `Page` it's
  attached to, present regardless of thread depth; a `parent` property, present only on a
  threaded reply, references another `Comment`. If a real export instead only sets
  `content`/`owner` on top-level comments and expects the owning page to be found by
  walking up the `parent` chain, replies will be dropped from the import (see
  `ResolveAllComments`'s remarks — an unplaceable comment is dropped, not guessed at).
  **They no longer vanish silently:** the reader counts them and the report carries a
  pipeline note saying how many, which is the signal that the assumed property name is
  wrong for your Confluence version. A large count there is the thing to act on. Still
  worth comparing the dry run's comment count against Confluence's own UI count for a
  handful of pages with active discussion threads.
- **Label shape.** Two shapes are checked (`ResolveLabels`): a direct `labels` collection
  of `Label` objects on the `Page`, and an indirect `labellings` collection of join objects
  each carrying a `label` reference. A real export may use only one, the other, or neither
  name. Namespaced label names (`"global:my-tag"`) are reduced to the part after the colon
  — confirm this matches what you'd expect the tag to display as.
- **Date format.** Assumed `java.util.Date`-style, parsed as UTC with no recoverable
  original time zone. Treat imported dates as approximate, not authoritative, regardless
  of what a real export turns out to use.
- **Space object cardinality.** The reader refuses (rather than guesses) if `entities.xml`
  contains zero or more than one `Space` object — confirm a single-space XML export
  (Space Tools → Content Tools → Export → XML) actually produces exactly one, not a
  full-site backup shape.

## 5a. Pages that will not convert at all

Distinct from a *lossy* page, which still produces Markdown plus report entries. A
body that is not well-formed XML, or that uses a named HTML entity outside the
converter's table (`&Aacute;`, `&oacute;`, `&frac12;`, `&dagger;` — most accented
characters), cannot be converted at all.

These are **isolated to the page or comment they occur on, and nothing is lost**. The
page is imported with its original Confluence storage-format body preserved verbatim
inside a fenced code block, and appears under "Pages needing review" with
`could not be converted` and the reason. The rest of the import continues, and the
page keeps its place in the tree so its children still import normally. A comment that
will not convert is skipped along with its replies, like any comment that fails to
create.

The preserved page needs converting by hand — the text is all there, in one code
block, with a note at the top of the page saying why.

A dry run finds every one of them without touching the database, which is the cheapest
way to deal with them: fix the source pages in Confluence, re-export, and re-run.
Otherwise, fix the placeholder pages by hand afterwards — the report lists them.

## 6. Known gaps that are not bugs to "fix" in this tool

- **Author attribution.** Every imported revision/comment/attachment is attributed to
  `--acting-user-id`. The real Confluence author is carried through the report
  (`OriginalAuthor`) but not applied anywhere — this needs a shadow-user mechanism
  (design.md §12's externally-flagged, never-loginable users) that doesn't exist yet.
  Don't build a workaround for this per-import; it's tracked centrally.
- **Label re-runs.** `ILabelService` has no way to look up an existing label by name — only
  create (fails if the name is taken) and attach. Within one run this never matters (each
  label name is created at most once and cached); re-running an import after a partial
  failure, where some labels from the first attempt already exist, will report those as
  failures rather than attaching to the pre-existing label. If you must re-run, expect to
  reconcile labels manually, or clear the partially-created space first.
- **There is no resume.** Nothing checkpoints an import: each service commits with its
  own SaveChanges, so a run that stops part-way leaves everything before that point in
  the database, and re-running the same export fails at space creation with "Space key
  already in use". Content problems no longer cause this — a page that will not convert
  is isolated and reported (§5a) — so what is left is infrastructure failure, where the
  recovery is to read the report (still written, exit code 2), delete the partial space,
  and start again. Cleaning up first also avoids the label gap below.
- **Confluence permissions are reported, never translated.** See §4 above. They are
  read and reported in full; nothing in this tool ever applies one.
- **Comments and page bodies share one `ConfluenceStorageConverter` instance and one
  `TwoPassPageIdResolver` per import** — a link from a comment to a page resolves exactly
  like a link from a page body would, including to pages created later in the same run.
