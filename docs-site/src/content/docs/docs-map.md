---
title: Documentation map
description: What lives where, why design.md is the source of truth, and how this site is generated.
---

RocketWiki's documentation culture has one load-bearing rule: **content is
never forked**. Each document has exactly one canonical home in the
repository, and this site is a build-time *view* of those documents, not a
copy anyone maintains.

## The canonical documents

| Document | Role | On this site |
|---|---|---|
| [`design.md`](https://github.com/veggielane/RocketWiki/blob/main/design.md) | **The constitution.** Architecture, content model, access control, audit, API, sync, deployment — the *why* behind everything. When code and design.md disagree, one of them is wrong and it's usually not design.md. | Split into [one page per §-section](/RocketWiki/design/00-overview/) |
| [`README.md`](https://github.com/veggielane/RocketWiki/blob/main/README.md) | **The status ledger.** How to build, and — load-bearing — the "Current status" section: an explicit account of what is verified by tests versus what has never actually been run. | Its status section is the [Current status](/RocketWiki/status/) page |
| [`data-model.md`](https://github.com/veggielane/RocketWiki/blob/main/data-model.md) | The concrete EF Core / SQL Server schema derived from the design. | [Data model](/RocketWiki/data-model/) |
| [`DEVELOPING.md`](https://github.com/veggielane/RocketWiki/blob/main/DEVELOPING.md) | Operational: the local dev loop, test tiers, running the stack. | [Developing locally](/RocketWiki/developing/) |
| [`deploy/README.md`](https://github.com/veggielane/RocketWiki/blob/main/deploy/README.md) | Operational: Helm chart, air-gapped image paths, the restore drill — and its own "what has never been verified" list. | [Deploying on k3s](/RocketWiki/operations/deploy/) |
| [`src/RocketWiki.AppHost/keycloak/README.md`](https://github.com/veggielane/RocketWiki/blob/main/src/RocketWiki.AppHost/keycloak/README.md) | Operational: the dev Keycloak realm and the mapper spec production must reproduce. | [Dev Keycloak realm](/RocketWiki/operations/keycloak/) |
| [`web/src/help/`](https://github.com/veggielane/RocketWiki/blob/main/web/src/help/) | **The user guide.** The in-app help the app itself serves at `/-/docs` — how to use and administer RocketWiki. Authored once; the app and this site both render it. | The [User Guide](/RocketWiki/guide/using-rocketwiki/getting-started/) pages |

## Why design.md is the source of truth

Every non-trivial decision in the codebase cites a design.md section —
`§6.7` for enforcement points, `§7` for audit, `§12` for sync. The document
is the shared contract between the many agents building the system in
parallel: a change that deviates from it is raised against the design first,
not slipped into code. This site preserves those `§N` references as links to
the corresponding split pages.

## How this site works

- A build-time script ([`docs-site/scripts/generate.mjs`](https://github.com/veggielane/RocketWiki/blob/main/docs-site/scripts/generate.mjs))
  splits `design.md` into per-section pages and imports the other documents,
  rewriting repo-relative links and `§N` references. Generated pages are
  gitignored — they exist only in the build.
- Only two pages are hand-written: the landing page and this one.
- **To change documentation, edit the canonical document** listed above (each
  generated page names its source at the top). Editing anything under the
  site's generated content is impossible to get wrong in a lasting way — it
  is overwritten on every build.

Details of the generation scheme are in
[`docs-site/README.md`](https://github.com/veggielane/RocketWiki/blob/main/docs-site/README.md).
