// Pure transformation logic for the docs-site content generator.
// No I/O in this file — everything here is unit-testable (scripts/lib.test.mjs)
// and deterministic: the same inputs always produce byte-identical output.
import { BASE, BLOB_BASE } from './site-constants.mjs';

// ---------------------------------------------------------------------------
// Slugs and anchors
// ---------------------------------------------------------------------------

/**
 * Anchor id for a Markdown heading, matching github-slugger (which both
 * GitHub and Astro's heading-id pass use): lowercase, punctuation removed,
 * spaces become hyphens, consecutive hyphens NOT collapsed.
 * "6.7 Enforcement points" -> "67-enforcement-points"
 */
export function anchorSlug(text) {
  return text
    .toLowerCase()
    .trim()
    .replace(/<[^>]+>/g, '')
    .replace(/[^\p{L}\p{N} _-]/gu, '')
    .replace(/ /g, '-');
}

/**
 * File/route slug (our own scheme, so we may be tidier than github-slugger):
 * collapse hyphen runs and trim them from the ends.
 * "Content model: WYSIWYG over Markdown" -> "content-model-wysiwyg-over-markdown"
 */
export function fileSlug(text) {
  return anchorSlug(text)
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
}

// ---------------------------------------------------------------------------
// Fence- and code-span-aware Markdown mechanics
// ---------------------------------------------------------------------------

/**
 * Returns a parallel array of booleans: is line i inside (or opening/closing)
 * a fenced code block? CommonMark-ish: a fence opens with >=3 backticks or
 * tildes at up to 3 spaces of indentation and closes with a run of the same
 * character at least as long, alone on its line.
 */
export function fencedLineMask(lines) {
  const mask = new Array(lines.length).fill(false);
  let fence = null; // { char, len }
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (fence === null) {
      if (m) {
        fence = { char: m[1][0], len: m[1].length };
        mask[i] = true;
      }
    } else {
      mask[i] = true;
      if (m && m[1][0] === fence.char && m[1].length >= fence.len && m[2].trim() === '') {
        fence = null;
      }
    }
  }
  return mask;
}

/**
 * Inline code spans of a single line as [start, endExclusive] ranges
 * (delimiters included). Implements CommonMark's backtick-run pairing (a
 * span opens and closes with runs of equal length), so ` ``` ` inside
 * single backticks is handled correctly.
 */
export function codeSpanRanges(line) {
  const runs = [...line.matchAll(/`+/g)];
  const spans = [];
  let i = 0;
  while (i < runs.length) {
    const open = runs[i];
    let matched = false;
    for (let j = i + 1; j < runs.length; j++) {
      if (runs[j][0].length === open[0].length) {
        spans.push([open.index, runs[j].index + runs[j][0].length]);
        i = j + 1;
        matched = true;
        break;
      }
    }
    if (!matched) i++;
  }
  return spans;
}

/** Applies `fn` to the parts of a single line NOT inside inline code spans. */
export function mapOutsideCodeSpans(line, fn) {
  const spans = codeSpanRanges(line);
  if (spans.length === 0) return fn(line);
  let out = '';
  let pos = 0;
  for (const [s, e] of spans) {
    out += fn(line.slice(pos, s)) + line.slice(s, e);
    pos = e;
  }
  out += fn(line.slice(pos));
  return out;
}

/** Applies `fn` line-by-line, skipping fenced code blocks and code spans. */
export function mapProse(text, fn) {
  const lines = text.split('\n');
  const fenced = fencedLineMask(lines);
  return lines
    .map((line, i) => (fenced[i] ? line : mapOutsideCodeSpans(line, fn)))
    .join('\n');
}

/**
 * Runs `fn` over a text segment with any existing complete Markdown links
 * protected by placeholders, so transformations can never nest a link inside
 * a link's text or destination.
 */
function withLinksProtected(segment, fn) {
  const saved = [];
  const protectedText = segment.replace(/\[[^\]]*\]\([^)]*\)/g, (m) => {
    saved.push(m);
    return `\x00${saved.length - 1}\x00`;
  });
  return fn(protectedText).replace(/\x00(\d+)\x00/g, (_, n) => saved[Number(n)]);
}

// ---------------------------------------------------------------------------
// design.md splitting
// ---------------------------------------------------------------------------

const SECTION_RE = /^## (\d+)\. (.+?)\s*$/;

/**
 * Splits design.md into its `## N. Title` sections.
 * Returns { intro, sections: [{ num, title, body }] } where body has had its
 * headings promoted one level (### -> ##) so each section page gets a proper
 * h2/h3 outline under Starlight's h1 page title. Heading anchors are
 * unaffected: ids derive from heading text, not depth.
 */
export function splitDesign(text) {
  const lines = text.split('\n');
  const fenced = fencedLineMask(lines);
  const sections = [];
  let intro = [];
  let current = null;
  for (let i = 0; i < lines.length; i++) {
    const m = fenced[i] ? null : lines[i].match(SECTION_RE);
    if (m) {
      current = { num: Number(m[1]), title: `${m[1]}. ${m[2]}`, bodyLines: [], bodyStart: i + 1 };
      sections.push(current);
    } else if (current) {
      current.bodyLines.push(lines[i]);
    } else {
      intro.push(lines[i]);
    }
  }
  return {
    intro: intro.join('\n').trim(),
    sections: sections.map((s) => {
      const body = s.bodyLines.join('\n');
      const bodyLines = body.split('\n');
      const bodyFenced = fencedLineMask(bodyLines);
      const promoted = bodyLines
        .map((l, i) => (bodyFenced[i] ? l : l.replace(/^(#{3,6}) /, (_, h) => `${h.slice(1)} `)))
        .join('\n')
        .trim();
      return { num: s.num, title: s.title, body: promoted };
    }),
  };
}

/** Extracts the body of `## <title>` up to the next `## ` heading or EOF. */
export function extractSection(text, title) {
  const lines = text.split('\n');
  const fenced = fencedLineMask(lines);
  let start = -1;
  for (let i = 0; i < lines.length; i++) {
    if (!fenced[i] && lines[i].trim() === `## ${title}`) {
      start = i + 1;
      break;
    }
  }
  if (start === -1) return null;
  let end = lines.length;
  for (let i = start; i < lines.length; i++) {
    if (!fenced[i] && /^## /.test(lines[i])) {
      end = i;
      break;
    }
  }
  return lines.slice(start, end).join('\n').trim();
}

// ---------------------------------------------------------------------------
// §N cross-reference linking
// ---------------------------------------------------------------------------

/**
 * Builds the §-reference lookup tables from split design sections.
 * - sectionPages: Map<number, routeSlug> e.g. 6 -> "design/06-access-control"
 * - anchors: Map<"6.7", { page, anchor }> for every numbered subsection
 *   heading (## 6.7 Foo, ### 6.4.1 Bar) found in the promoted bodies.
 */
export function buildSectionIndex(sections) {
  const sectionPages = new Map();
  const anchors = new Map();
  for (const s of sections) {
    const titleText = s.title.replace(/^\d+\.\s*/, '');
    const page = `design/${String(s.num).padStart(2, '0')}-${fileSlug(titleText)}`;
    sectionPages.set(s.num, page);
    const lines = s.body.split('\n');
    const fenced = fencedLineMask(lines);
    for (let i = 0; i < lines.length; i++) {
      if (fenced[i]) continue;
      const m = lines[i].match(/^#{2,6} (\d+(?:\.\d+)+) .+$/);
      if (m) {
        anchors.set(m[1], { page, anchor: anchorSlug(lines[i].replace(/^#+ /, '')) });
      }
    }
  }
  return { sectionPages, anchors };
}

const SECTION_REF_RE = /(design\.md\s+)?§\s?(\d+)((?:\.\d+)+)?/g;

/**
 * Best-effort pass turning `§N` / `§N.M` / `design.md §N` prose references
 * into links to the split design pages. Skips fenced code, inline code, and
 * anything already inside a Markdown link. Ambiguous references (a section
 * number the split didn't produce) are left as plain text and reported.
 */
export function linkifySectionRefs(text, index, report) {
  return mapProse(text, (segment) =>
    withLinksProtected(segment, (t) =>
      t.replace(SECTION_REF_RE, (whole, prefix, major, minors) => {
        const ref = major + (minors ?? '');
        const page = index.sectionPages.get(Number(major));
        if (!page) {
          report.unlinkedRefs.push(`§${ref}`);
          return whole;
        }
        let url = `${BASE}/${page}/`;
        if (minors) {
          const a = index.anchors.get(ref);
          if (a) url += `#${a.anchor}`;
          else report.sectionTopFallbacks.push(`§${ref}`);
        }
        report.linkedRefs++;
        return `[${prefix ? 'design.md ' : ''}§${ref}](${url})`;
      })
    )
  );
}

// ---------------------------------------------------------------------------
// Relative repo-link rewriting
// ---------------------------------------------------------------------------

function posixJoin(dir, rel) {
  const parts = (dir ? dir.split('/') : []).concat(rel.split('/'));
  const out = [];
  for (const p of parts) {
    if (p === '' || p === '.') continue;
    if (p === '..') out.pop();
    else out.push(p);
  }
  return out.join('/');
}

/**
 * Rewrites relative repo links in an imported doc:
 * - a target that exists as a generated site page -> site URL (base-prefixed)
 * - any other repo-relative target -> GitHub blob URL
 * Scheme URLs (https:, page:, mailto:), same-page `#anchors`, and
 * root-absolute `/...` destinations are left untouched.
 */
export function rewriteRepoLinks(text, sourceDir, pageMap, report) {
  const lines = text.split('\n');
  const fenced = fencedLineMask(lines);
  const rewritten = lines.map((line, i) => {
    if (fenced[i]) return line;
    // A link label may itself contain inline code ([`data-model.md`](...)),
    // so match on the whole line and skip only links that START inside a
    // code span (i.e. the link syntax is being quoted as an example).
    const spans = codeSpanRanges(line);
    return line.replace(/\[([^\]]*)\]\(([^)\s]+)\)/g, (whole, label, dest, offset) => {
      if (spans.some(([s, e]) => offset >= s && offset < e)) return whole;
      if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(dest)) return whole; // scheme (https:, page:, ...)
      if (dest.startsWith('#') || dest.startsWith('/')) return whole;
      const [pathPart, ...hashParts] = dest.split('#');
      const hash = hashParts.length ? `#${hashParts.join('#')}` : '';
      const repoPath = posixJoin(sourceDir, pathPart);
      const mapped = pageMap.get(repoPath);
      if (mapped) {
        if (hash) report.droppedAnchors.push(`${repoPath}${hash}`);
        report.siteRewrites.push(`${dest} -> ${BASE}/${mapped}/`);
        return `[${label}](${BASE}/${mapped}/)`;
      }
      report.blobRewrites.push(`${dest} -> ${BLOB_BASE}/${repoPath}${hash}`);
      return `[${label}](${BLOB_BASE}/${repoPath}${hash})`;
    });
  });
  return rewritten.join('\n');
}

// ---------------------------------------------------------------------------
// Page assembly
// ---------------------------------------------------------------------------

/** YAML-safe frontmatter + generated-file marker + body. */
export function buildPage({ title, description, notice, body }) {
  const fm = [`title: ${JSON.stringify(title)}`];
  if (description) fm.push(`description: ${JSON.stringify(description)}`);
  return [
    '---',
    ...fm,
    '---',
    '',
    '<!-- GENERATED FILE — do not edit. Produced by docs-site/scripts/generate.mjs; the canonical source is named below. -->',
    '',
    ...(notice ? [notice, ''] : []),
    body,
    '',
  ].join('\n');
}

/** Collects every heading anchor in a final page body (for link validation). */
export function collectAnchors(body) {
  const lines = body.split('\n');
  const fenced = fencedLineMask(lines);
  const anchors = new Set();
  for (let i = 0; i < lines.length; i++) {
    if (fenced[i]) continue;
    const m = lines[i].match(/^#{1,6} (.+)$/);
    if (m) {
      // Anchor ids derive from rendered text: strip link markup first.
      anchors.add(anchorSlug(m[1].replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')));
    }
  }
  return anchors;
}

/** GitHub's anchor for a `## N. Title` heading in design.md (for blob deep links). */
export function githubHeadingAnchor(headingText) {
  return anchorSlug(headingText);
}

export function newReport() {
  return {
    linkedRefs: 0,
    unlinkedRefs: [],
    sectionTopFallbacks: [],
    siteRewrites: [],
    blobRewrites: [],
    droppedAnchors: [],
  };
}
