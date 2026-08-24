#!/usr/bin/env node
// Build-time content generation for the RocketWiki docs site.
//
// The site never forks content: design.md, README.md ("Current status"),
// DEVELOPING.md, data-model.md, deploy/README.md and the Keycloak realm
// README stay canonical at their repo locations. This script re-emits them
// as Starlight pages under src/content/docs/** on every dev/build run
// (`predev` / `prebuild` hooks). Everything it writes is gitignored.
//
// Usage:
//   node scripts/generate.mjs           regenerate all pages
//   node scripts/generate.mjs --check   validate only (writes nothing);
//                                       exits non-zero on any problem
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BASE, BLOB_BASE } from './site-constants.mjs';
import {
  buildPage,
  buildSectionIndex,
  collectAnchors,
  extractSection,
  githubHeadingAnchor,
  linkifySectionRefs,
  newReport,
  rewriteRepoLinks,
  splitDesign,
} from './lib.mjs';

const docsSiteRoot = path.resolve(fileURLToPath(import.meta.url), '..', '..');
const repoRoot = path.resolve(docsSiteRoot, '..');
const outRoot = path.join(docsSiteRoot, 'src', 'content', 'docs');

// Curated (hand-written, committed) pages — everything else under
// src/content/docs is owned by this script.
const CURATED_ROUTES = ['', 'docs-map'];
const CURATED_FILES = ['index.mdx', 'docs-map.md'];

// Canonical sources, relative to the repo root.
const SOURCES = {
  design: 'design.md',
  readme: 'README.md',
  developing: 'DEVELOPING.md',
  dataModel: 'data-model.md',
  deploy: 'deploy/README.md',
  keycloak: 'src/RocketWiki.AppHost/keycloak/README.md',
};

function fail(msg) {
  console.error(`docs-site generate: ERROR: ${msg}`);
  process.exit(1);
}

function readSource(rel) {
  const abs = path.join(repoRoot, rel);
  if (!fs.existsSync(abs)) {
    fail(`canonical source missing: ${rel} (looked at ${abs}). ` +
      'The site generates from the repo docs — it cannot build without them.');
  }
  return fs.readFileSync(abs, 'utf8').replace(/\r\n/g, '\n');
}

/** Splits an imported doc into H1 title + remainder. */
function titleAndBody(text, sourceRel) {
  const m = text.match(/^# (.+?)\s*\n/);
  if (!m) fail(`${sourceRel}: expected a leading "# Title" heading`);
  return { title: m[1], body: text.slice(m.index + m[0].length).trim() };
}

function noticeLine(sourceRel, anchor = '') {
  return `*Generated at build time from [\`${sourceRel}\`](${BLOB_BASE}/${sourceRel}${anchor}) — edit that file, not this page.*`;
}

/** Builds every generated page in memory. Pure given the source texts. */
export function buildAll() {
  const report = newReport();
  const texts = Object.fromEntries(
    Object.entries(SOURCES).map(([k, rel]) => [k, readSource(rel)])
  );

  const { intro, sections } = splitDesign(texts.design);
  if (sections.length === 0) {
    fail(`design.md split produced zero "## N. Title" sections — the split heuristic no longer matches the document.`);
  }
  const index = buildSectionIndex(sections);

  // Repo path -> site route, for rewriting relative links in imported docs.
  // README.md itself deliberately maps to GitHub (only its "Current status"
  // section lives on the site) — so it is absent here.
  const pageMap = new Map([
    [SOURCES.design, 'design/00-overview'],
    [SOURCES.dataModel, 'data-model'],
    [SOURCES.developing, 'developing'],
    [SOURCES.deploy, 'operations/deploy'],
    [SOURCES.keycloak, 'operations/keycloak'],
  ]);

  const pipeline = (text, sourceDir) =>
    rewriteRepoLinks(linkifySectionRefs(text, index, report), sourceDir, pageMap, report);

  const files = new Map(); // route -> file content ("" route not used here)

  // --- design.md: overview + one page per ## N. section -------------------
  const introBody = titleAndBody(intro, SOURCES.design);
  files.set(
    'design/00-overview',
    buildPage({
      title: 'Design document — overview',
      description: 'design.md is the constitution: the following pages are its sections, split at build time.',
      notice: noticeLine(SOURCES.design),
      body:
        pipeline(introBody.body, '') +
        '\n\nThe pages in this sidebar group are the numbered sections of ' +
        `[\`design.md\`](${BLOB_BASE}/${SOURCES.design}), split one page per section at build time. ` +
        '`§N` cross-references throughout the site link back to them.',
    })
  );
  for (const s of sections) {
    const route = index.sectionPages.get(s.num);
    files.set(
      route,
      buildPage({
        title: s.title,
        description: `design.md §${s.num}, split at build time.`,
        notice: noticeLine(SOURCES.design, `#${githubHeadingAnchor(s.title)}`),
        body: pipeline(s.body, ''),
      })
    );
  }

  // --- whole-document imports ---------------------------------------------
  const imports = [
    { key: 'developing', route: 'developing', sourceDir: '', description: 'How to build, test, and run RocketWiki locally.' },
    { key: 'dataModel', route: 'data-model', sourceDir: '', description: 'The concrete EF Core / SQL Server schema.' },
    { key: 'deploy', route: 'operations/deploy', sourceDir: 'deploy', description: 'Helm chart, air-gapped image paths, and the restore drill.' },
    { key: 'keycloak', route: 'operations/keycloak', sourceDir: 'src/RocketWiki.AppHost/keycloak', description: 'The dev realm, its protocol mappers, and what production Keycloak must reproduce.' },
  ];
  for (const { key, route, sourceDir, description } of imports) {
    const { title, body } = titleAndBody(texts[key], SOURCES[key]);
    files.set(
      route,
      buildPage({
        title,
        description,
        notice: noticeLine(SOURCES[key]),
        body: pipeline(body, sourceDir),
      })
    );
  }

  // --- README "Current status" -> Status page ------------------------------
  const status = extractSection(texts.readme, 'Current status');
  if (!status) {
    fail('README.md has no "## Current status" section — the Status page cannot be generated.');
  }
  const sha = process.env.GITHUB_SHA;
  const provenance = sha
    ? `built from commit [\`${sha.slice(0, 7)}\`](${BLOB_BASE.replace('/blob/main', '')}/blob/${sha}/README.md#current-status)`
    : 'built from a local working tree';
  files.set(
    'status',
    buildPage({
      title: 'Current status',
      description: 'What has and hasn\'t actually been verified — imported from the repository README.',
      notice: [
        ':::note[Auto-generated]',
        `This page is the "[Current status](${BLOB_BASE}/README.md#current-status)" section of the repository README, imported verbatim at build time (${provenance}). The README is the status ledger — edit it, not this page.`,
        ':::',
      ].join('\n'),
      body: pipeline(status, ''),
    })
  );

  return { files, report, sectionCount: sections.length };
}

// ---------------------------------------------------------------------------
// Validation (--check) and writing
// ---------------------------------------------------------------------------

function validate(files) {
  const problems = [];
  const routes = new Set([...files.keys(), ...CURATED_ROUTES]);
  const anchorsByRoute = new Map(
    [...files].map(([route, content]) => [route, collectAnchors(content)])
  );
  const internalLink = new RegExp(`\\]\\((${BASE.replace('/', '\\/')}\\/[^)#]*)(#[^)]*)?\\)`, 'g');

  const check = (label, content) => {
    for (const m of content.matchAll(internalLink)) {
      const route = m[1].slice(BASE.length + 1).replace(/\/$/, '');
      if (!routes.has(route)) {
        problems.push(`${label}: link to unknown page ${m[1]}`);
        continue;
      }
      if (m[2] && anchorsByRoute.has(route)) {
        const anchor = m[2].slice(1);
        if (!anchorsByRoute.get(route).has(anchor)) {
          problems.push(`${label}: link to ${route} with unknown anchor #${anchor}`);
        }
      }
    }
  };

  for (const [route, content] of files) check(`generated ${route}`, content);
  // Curated pages hardcode the base path in their links — validate them too.
  for (const f of CURATED_FILES) {
    const abs = path.join(outRoot, f);
    if (!fs.existsSync(abs)) {
      problems.push(`curated page missing: src/content/docs/${f}`);
      continue;
    }
    check(`curated ${f}`, fs.readFileSync(abs, 'utf8'));
  }
  return problems;
}

function write(files) {
  // Idempotent: clear exactly what this script owns, then re-emit.
  for (const owned of ['design', 'operations', 'status.md', 'developing.md', 'data-model.md']) {
    fs.rmSync(path.join(outRoot, owned), { recursive: true, force: true });
  }
  for (const [route, content] of files) {
    const abs = path.join(outRoot, `${route}.md`);
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs, content, 'utf8');
  }
}

function printReport(report, files, sectionCount) {
  const uniq = (a) => [...new Set(a)];
  console.log(`docs-site generate: ${files.size} pages (${sectionCount} design sections + overview + 5 imports).`);
  console.log(`  §-references linked: ${report.linkedRefs}`);
  if (report.sectionTopFallbacks.length > 0) {
    console.log(`  §N.M linked to section top (no matching numbered heading): ${uniq(report.sectionTopFallbacks).join(', ')}`);
  }
  if (report.unlinkedRefs.length > 0) {
    console.log(`  § left as plain text (no such section): ${uniq(report.unlinkedRefs).join(', ')}`);
  }
  if (report.siteRewrites.length > 0) {
    console.log(`  repo links -> site pages: ${uniq(report.siteRewrites).join('; ')}`);
  }
  if (report.blobRewrites.length > 0) {
    console.log(`  repo links -> GitHub blob: ${uniq(report.blobRewrites).join('; ')}`);
  }
  if (report.droppedAnchors.length > 0) {
    console.log(`  WARNING: anchors dropped when remapping to site pages: ${uniq(report.droppedAnchors).join(', ')}`);
  }
}

const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const checkOnly = process.argv.includes('--check');
  const { files, report, sectionCount } = buildAll();
  const problems = validate(files);
  // §-reference hygiene is a build failure, not a footnote. A §N.M with no
  // matching numbered heading silently linked to the section top, and a §N
  // naming a section that doesn't exist stayed plain text — both used to be
  // print-only, which let broken cross-references in the canonical docs pass
  // --check. Fix the doc (or add the missing numbered heading); don't ship
  // the fallback.
  const uniq = (a) => [...new Set(a)];
  if (report.sectionTopFallbacks.length > 0) {
    problems.push(
      `§N.M reference(s) with no matching numbered heading (would link to section top): ${uniq(report.sectionTopFallbacks).join(', ')}`
    );
  }
  if (report.unlinkedRefs.length > 0) {
    problems.push(
      `§ reference(s) to nonexistent sections (would stay plain text): ${uniq(report.unlinkedRefs).join(', ')}`
    );
  }
  printReport(report, files, sectionCount);
  if (problems.length > 0) {
    for (const p of problems) console.error(`docs-site generate: ERROR: ${p}`);
    process.exit(1);
  }
  if (checkOnly) {
    console.log('docs-site generate: --check passed; nothing written.');
  } else {
    write(files);
    console.log(`docs-site generate: wrote ${files.size} pages under src/content/docs/.`);
  }
}
