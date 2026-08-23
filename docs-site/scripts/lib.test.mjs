import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  anchorSlug,
  buildSectionIndex,
  collectAnchors,
  extractSection,
  fileSlug,
  linkifySectionRefs,
  mapOutsideCodeSpans,
  newReport,
  rewriteRepoLinks,
  splitDesign,
} from './lib.mjs';

// ---------------------------------------------------------------------------
// slugs
// ---------------------------------------------------------------------------

test('anchorSlug matches github-slugger for numbered subsection headings', () => {
  assert.equal(anchorSlug('6.7 Enforcement points'), '67-enforcement-points');
  assert.equal(anchorSlug('6.4.1 Structural operations touch more than one page'),
    '641-structural-operations-touch-more-than-one-page');
});

test('fileSlug tidies punctuation-heavy titles', () => {
  assert.equal(fileSlug('Content model: WYSIWYG over Markdown'), 'content-model-wysiwyg-over-markdown');
  assert.equal(fileSlug('Multi-instance sync (low → high)'), 'multi-instance-sync-low-high');
});

// ---------------------------------------------------------------------------
// code-span awareness (the ` ```mermaid ` inside single backticks case)
// ---------------------------------------------------------------------------

test('mapOutsideCodeSpans pairs backtick runs CommonMark-style', () => {
  const line = 'Diagrams | ` ```mermaid ` fenced block (§12) and `code §13` end §14';
  const out = mapOutsideCodeSpans(line, (t) => t.replaceAll('§', 'X'));
  assert.equal(out, 'Diagrams | ` ```mermaid ` fenced block (X12) and `code §13` end X14');
});

// ---------------------------------------------------------------------------
// design.md splitting
// ---------------------------------------------------------------------------

const design = [
  '# RocketWiki — Design Document',
  '',
  'Preamble.',
  '',
  '## 6. Access control',
  '',
  'See §7 and §6.7 for details.',
  '',
  '### 6.7 Enforcement points',
  '',
  'Body. Fenced heading must not split:',
  '```',
  '## 7. Not a section',
  '§99 not linked here',
  '```',
  '',
  '## 7. Audit logging',
  '',
  'Refers back to design.md §6.4.1 (missing) and §42 (nonexistent).',
].join('\n');

test('splitDesign splits on ## N. headings, promotes subheadings, keeps fences intact', () => {
  const { intro, sections } = splitDesign(design);
  assert.match(intro, /Preamble\./);
  assert.equal(sections.length, 2);
  assert.deepEqual(sections.map((s) => s.title), ['6. Access control', '7. Audit logging']);
  assert.match(sections[0].body, /^## 6\.7 Enforcement points$/m); // promoted ### -> ##
  assert.match(sections[0].body, /^## 7\. Not a section$/m); // inside fence, untouched
});

test('splitDesign on a doc with no sections yields zero sections (generator fails loudly)', () => {
  assert.equal(splitDesign('# Just a title\n\nProse only.').sections.length, 0);
});

// ---------------------------------------------------------------------------
// § linking
// ---------------------------------------------------------------------------

test('linkifySectionRefs links §N and §N.M, skips code, reports the ambiguous', () => {
  const { sections } = splitDesign(design);
  const index = buildSectionIndex(sections);
  assert.equal(index.sectionPages.get(6), 'design/06-access-control');
  assert.deepEqual(index.anchors.get('6.7'), { page: 'design/06-access-control', anchor: '67-enforcement-points' });

  const report = newReport();
  const out = linkifySectionRefs(sections[0].body + '\n' + sections[1].body, index, report);
  assert.match(out, /\[§7\]\(\/RocketWiki\/design\/07-audit-logging\/\)/);
  assert.match(out, /\[§6\.7\]\(\/RocketWiki\/design\/06-access-control\/#67-enforcement-points\)/);
  // "design.md §6.4.1": section 6 exists but no 6.4.1 heading -> section top + report
  assert.match(out, /\[design\.md §6\.4\.1\]\(\/RocketWiki\/design\/06-access-control\/\)/);
  assert.deepEqual(report.sectionTopFallbacks, ['§6.4.1']);
  // §42 has no section -> untouched plain text, reported
  assert.match(out, /§42 \(nonexistent\)/);
  assert.deepEqual(report.unlinkedRefs, ['§42']);
  // fenced content untouched
  assert.match(out, /§99 not linked here/);
});

test('linkifySectionRefs never nests inside an existing markdown link', () => {
  const index = buildSectionIndex(splitDesign(design).sections);
  const report = newReport();
  const out = linkifySectionRefs('See [the §6 chapter](https://example.com) and §6.', index, report);
  assert.match(out, /\[the §6 chapter\]\(https:\/\/example\.com\)/);
  assert.match(out, /\[§6\]\(\/RocketWiki\/design\/06-access-control\/\)/);
});

// ---------------------------------------------------------------------------
// relative link rewriting
// ---------------------------------------------------------------------------

test('rewriteRepoLinks maps known docs to site pages, others to GitHub blob, schemes untouched', () => {
  const pageMap = new Map([
    ['data-model.md', 'data-model'],
    ['src/RocketWiki.AppHost/keycloak/README.md', 'operations/keycloak'],
  ]);
  const report = newReport();
  const text = [
    'See [`data-model.md`](data-model.md) and [web docs](web/README.md).',
    'From deploy: [realm](../src/RocketWiki.AppHost/keycloak/README.md).',
    'Leave [page link](page://abc123) and [anchor](#current-status) and [ext](https://example.com) alone.',
  ].join('\n');
  const out = rewriteRepoLinks(text, 'deploy', pageMap, report);
  assert.match(out, /\[realm\]\(\/RocketWiki\/operations\/keycloak\/\)/);
  // resolved against sourceDir=deploy, so data-model.md -> deploy/data-model.md -> blob URL
  assert.match(out, /\[`data-model\.md`\]\(https:\/\/github\.com\/veggielane\/RocketWiki\/blob\/main\/deploy\/data-model\.md\)/);
  assert.match(out, /\[web docs\]\(https:\/\/github\.com\/veggielane\/RocketWiki\/blob\/main\/deploy\/web\/README\.md\)/);
  assert.match(out, /\(page:\/\/abc123\)/);
  assert.match(out, /\(#current-status\)/);
  assert.match(out, /\(https:\/\/example\.com\)/);

  const rootOut = rewriteRepoLinks('[dm](data-model.md)', '', pageMap, report);
  assert.match(rootOut, /\[dm\]\(\/RocketWiki\/data-model\/\)/);
});

test('rewriteRepoLinks leaves link syntax quoted inside code spans alone', () => {
  const report = newReport();
  const out = rewriteRepoLinks('Write `[text](data-model.md)` to link it.', '', new Map([['data-model.md', 'data-model']]), report);
  assert.equal(out, 'Write `[text](data-model.md)` to link it.');
});

// ---------------------------------------------------------------------------
// section extraction + anchor collection
// ---------------------------------------------------------------------------

test('extractSection returns the section body up to the next ## heading', () => {
  const text = '# T\n\n## Alpha\n\na\n\n## Current status\n\nthe status\n\nmore';
  assert.equal(extractSection(text, 'Current status'), 'the status\n\nmore');
  assert.equal(extractSection(text, 'Alpha'), 'a');
  assert.equal(extractSection(text, 'Missing'), null);
});

test('collectAnchors strips link markup before slugging', () => {
  const anchors = collectAnchors('## 6.7 See [§8](/RocketWiki/design/08-api-design/) here\n```\n## not me\n```');
  assert.ok(anchors.has('67-see-8-here'));
  assert.equal(anchors.size, 1);
});
