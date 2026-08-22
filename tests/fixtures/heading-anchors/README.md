# Heading anchor fixture corpus

Cross-language contract for the heading-anchor algorithm (design.md §9):
`SearchHit.anchorId` (backend) and the ids `RichTextEditor` stamps onto
rendered headings (frontend, `web/src/editor/headingAnchors.ts`) must be
**identical** for the same page, or every search deep link silently lands
on the wrong section — a bug nobody notices until a user reports "the link
took me to the wrong place."

Each `{name}.json` file here is one test case:

```json
{
  "headings": [{ "level": 1, "text": "Intro" }, { "level": 2, "text": "Setup" }],
  "anchors": ["intro", "intro--setup"]
}
```

- `headings` — every heading on a page, **in document order**, as
  `{ level, text }`. `level` is the heading level (1–6); `text` is the
  heading's plain text content.
- `anchors` — the expected anchor id for each heading, same order,
  produced by the canonical algorithm in
  `web/src/editor/headingAnchors.ts` (`computeHeadingAnchors`).

These files are **regenerated on every frontend test run**
(`web/src/editor/__tests__/headingAnchorsCorpus.test.ts`) from the fixture
list in `headingAnchorsCorpus.ts`, so they can never hand-drift from what
the TypeScript implementation actually produces — same "regenerate, don't
hand-maintain" pattern as `tests/fixtures/converted-markdown` (see
`tests/RocketWiki.Importer.Tests/Corpus`).

**For whoever implements the backend resolver:** port the algorithm in
`headingAnchors.ts` exactly — don't write an equivalent slugifier from
scratch. Two independently-written implementations will agree on ordinary
ASCII headings and quietly diverge on punctuation stripping, empty/
non-ASCII fallback (`only-punctuation-heading-falls-back-to-section.json`,
`non-ascii-heading-strips-to-section.json` are exactly this case — both
fall back to the literal string `"section"`, not a transliteration), and
duplicate-path disambiguation ordinals. Load each `.json` file here, run
your implementation over its `headings`, and assert equality with
`anchors` — that turns "must match exactly" into a test that fails loudly
instead of a deep link that fails silently.
