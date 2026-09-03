import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { fileSlug } from './lib.mjs';
import { buildAll } from './generate.mjs';

// buildAll reads the real repo docs (design.md, the in-app help, ...) — the same
// thing the CLI does, minus the write. These tests assert the user-guide half of
// that output: that the site serves the in-app help verbatim, so "the same
// content in both" is checked, not assumed.
const repoRoot = path.resolve(fileURLToPath(import.meta.url), '..', '..', '..');
const { files } = buildAll();

function manifest() {
  return JSON.parse(fs.readFileSync(path.join(repoRoot, 'web/src/help/manifest.json'), 'utf8'));
}

/** The source topic file with its leading "# Title" line removed, trimmed. */
function sourceBody(slug) {
  const text = fs.readFileSync(path.join(repoRoot, 'web/src/help/content', `${slug}.md`), 'utf8').replace(/\r\n/g, '\n');
  return text.replace(/^#[^\n]*\n/, '').trim();
}

test('every manifest topic becomes a User guide page under its section', () => {
  for (const section of manifest().sections) {
    const dir = fileSlug(section.title);
    section.topics.forEach((topic, order) => {
      const route = `guide/${dir}/${topic.slug}`;
      assert.ok(files.has(route), `expected a generated page at ${route}`);
      const page = files.get(route);
      assert.match(page, /^---\n/, `${route} should open with frontmatter`);
      // A literal check, not a RegExp: a summary is prose and may carry regex
      // metacharacters ("(protected).") that would otherwise silently never match.
      assert.ok(page.includes(`description: ${JSON.stringify(topic.summary)}`), `${route} description = manifest summary`);
      assert.match(page, new RegExp(`\\n  order: ${order}\\n`), `${route} sidebar.order = manifest position`);
    });
  }
});

test('a guide page contains its in-app source verbatim (content is not forked)', () => {
  // getting-started has no links or §-refs, so the pipeline is a passthrough:
  // the served body must be byte-identical to the file the SPA renders at /-/docs.
  const page = files.get('guide/using-rocketwiki/getting-started');
  assert.ok(page.includes(sourceBody('getting-started')),
    'the generated page must contain the in-app help body unchanged');
});

test('a guide page takes its title from the source H1 and cites the source file', () => {
  const page = files.get('guide/using-rocketwiki/getting-started');
  assert.match(page, /title: "Getting started"/, 'title comes from the file H1');
  assert.match(page, /web\/src\/help\/content\/getting-started\.md/, 'the page names its canonical source');
});

test('both manifest sections are represented', () => {
  assert.ok(files.has('guide/using-rocketwiki/getting-started'));
  assert.ok(files.has('guide/administering-rocketwiki/admin-instance'));
});
