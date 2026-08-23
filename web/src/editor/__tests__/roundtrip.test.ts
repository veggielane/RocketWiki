import { describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import { editorExtensions } from '../extensions'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'

/**
 * The round-trip rule (design.md §4): Markdown -> editor -> Markdown must
 * be byte-identical. This drives markdown through a *real* headless TipTap
 * `Editor` instance (not just our own parse/serialize functions talking to
 * each other) so the test also proves the JSON is valid against the actual
 * schema the WYSIWYG editor presents to users.
 */
function roundTrip(markdown: string): string {
  const editor = new Editor({
    extensions: editorExtensions,
    content: markdownToJson(markdown),
  })
  try {
    return jsonToMarkdown(editor.getJSON())
  } finally {
    editor.destroy()
  }
}

describe('Markdown round trip — required v1 feature set (design.md §4)', () => {
  const cases: [name: string, markdown: string][] = [
    ['heading levels', '# H1\n\n## H2\n\n### H3\n'],
    ['bold / italic / strike', 'Some **bold**, *italic*, and ~~struck~~ text.\n'],
    ['overlapping bold + italic', '**a *b* c**\n'],
    ['inline code', 'Run `npm test` to check.\n'],
    ['bullet list', '- one\n- two\n- three\n'],
    ['ordered list from 1', '1. one\n2. two\n3. three\n'],
    ['ordered list custom start', '5. five\n6. six\n'],
    ['nested bullet list', '- one\n  - nested a\n  - nested b\n- two\n'],
    ['blockquote single paragraph', '> quoted text\n'],
    ['blockquote multiple paragraphs', '> first\n>\n> second\n'],
    ['link (http)', 'See [the docs](https://example.com/docs).\n'],
    ['link with title', 'See [the docs](https://example.com/docs "Docs").\n'],
    ['pipe table', '| a | b |\n| --- | --- |\n| 1 | 2 |\n'],
    ['pipe table multi-row', '| Name | Age |\n| --- | --- |\n| Ada | 36 |\n| Alan | 41 |\n'],
    ['fenced code block with language', '```js\nconst a = 1;\nconsole.log(a);\n```\n'],
    ['fenced code block no language', '```\nplain text\n```\n'],
    // Diagrams (design.md §17 resolution): both are *plain fenced blocks*
    // in Markdown — rendering is a NodeView concern; the serializer, sync
    // bundles, and the Confluence importer treat them as inert text.
    ['mermaid fence (rendered as a diagram, stored as a plain fence)', '```mermaid\ngraph TD\n  A --> B\n```\n'],
    [
      'mermaid fence with blank line and non-mermaid-looking text (payload is opaque)',
      '```mermaid\nsequenceDiagram\n\nAlice->>Bob: hi **not markdown**\n```\n',
    ],
    [
      'drawio fence (base64 editable-SVG payload)',
      '```drawio\nPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIGNvbnRlbnQ9IiZsdDtteGZpbGUmZ3Q7Jmx0Oy9teGZpbGUmZ3Q7Ij48cmVjdCB3aWR0aD0iMTAiIGhlaWdodD0iMTAiLz48L3N2Zz4=\n```\n',
    ],
    [
      'drawio fence with a hand-wrapped (multi-line) payload survives verbatim',
      '```drawio\nPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5v\ncmcvMjAwMC9zdmciLz4=\n```\n',
    ],
    [
      'drawio fence with a payload that is not even base64 survives verbatim (renders as an inline error, never lost)',
      '```drawio\nnot really base64!!\n```\n',
    ],
    ['empty drawio fence (freshly inserted, not yet drawn)', '```drawio\n\n```\n'],
    ['task list', '- [ ] todo\n- [x] done\n'],
    ['image / attachment', '![diagram](attachment://file-123)\n'],
    ['image / attachment with title', '![diagram](attachment://file-123 "A diagram")\n'],
    ['callout info', ':::info\nSomething important.\n:::\n'],
    ['callout warning', ':::warning\nBe careful.\n:::\n'],
    ['callout note', ':::note\nFor the record.\n:::\n'],
    ['callout with multiple paragraphs', ':::info\nFirst.\n\nSecond.\n:::\n'],
    ['page link', 'See [Getting Started](page://page-abc).\n'],
    ['mention', 'Thanks @[Ada Lovelace](user://user-42) for the review.\n'],
    ['horizontal rule (bonus, not in v1 table but trivial + prevents data loss)', 'above\n\n---\n\nbelow\n'],
    [
      'compound: link + page link + mention in one paragraph',
      'Ping @[Ada Lovelace](user://user-42) re [the docs](https://example.com) and [Setup](page://page-1).\n',
    ],
    ['callout containing a list', ':::warning\nCheck before merging:\n\n- tests pass\n- docs updated\n:::\n'],
    ['table cell with bold text', '| Name | Status |\n| --- | --- |\n| Ada | **done** |\n'],
    [
      'code block with blank line',
      '```js\nfunction f() {\n  return 1;\n}\n\nf();\n```\n',
    ],
    ['task list item with nested sublist', '- [ ] parent\n  - child a\n  - child b\n- [x] other\n'],
    ['ordered list with nested bullet list', '1. one\n   - sub a\n   - sub b\n2. two\n'],
    [
      'ordered list crossing double-digit indent width',
      '9. nine\n   - sub\n10. ten\n    - sub\n',
    ],
    [
      'full document, multiple feature types together',
      [
        '# Onboarding',
        '',
        'Welcome, @[Ada Lovelace](user://user-42). See [the setup guide](page://page-1).',
        '',
        ':::info',
        'Read this first.',
        ':::',
        '',
        '## Checklist',
        '',
        '- [ ] Read the docs',
        '- [x] Get access',
        '',
        '```bash',
        'npm install',
        '```',
        '',
        '```mermaid',
        'graph LR',
        '  start --> done',
        '```',
        '',
        '```drawio',
        'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=',
        '```',
        '',
        '| Step | Owner |',
        '| --- | --- |',
        '| Setup | Ada |',
        '',
        '> Ask in #onboarding if stuck.',
        '',
      ].join('\n'),
    ],
  ]

  it.each(cases)('%s', (_name, markdown) => {
    expect(roundTrip(markdown)).toBe(markdown)
  })
})

describe('Known, documented round-trip limitations', () => {
  it('alternate emphasis delimiters normalize to the canonical form (not byte-identical)', () => {
    // `*text*` and `_text_` are both valid CommonMark italics; markdown-it
    // reports both as the same `em_open`/`em_close` token type without
    // preserving which character was used. Our serializer always emits the
    // canonical `*..*` / `**...**` forms (see toMarkdown.ts and design.md
    // §4's "Canonical normalization" table), so content authored elsewhere
    // with the other valid delimiter gets silently normalized on first
    // save. This is a deliberate, accepted simplification: the rendered
    // result is identical, only the delimiter character in the stored
    // Markdown changes, and the editor itself can never *produce* the
    // non-canonical delimiter, so this only affects content ingested from
    // outside the editor (e.g. Confluence migration, hand-edited files) on
    // its first save.
    const underscoreItalic = '_italic_\n'
    const underscoreBold = '__bold__\n'
    expect(roundTrip(underscoreItalic)).toBe('*italic*\n')
    expect(roundTrip(underscoreBold)).toBe('**bold**\n')
  })

  it('ordered lists using repeated "1." markers renumber sequentially (not byte-identical)', () => {
    // GFM lets every item say "1." and leaves rendering order to the
    // renderer. ProseMirror/TipTap's OrderedList node has no per-item
    // marker attribute — only a single list-level `start` — so this
    // authoring style can't be represented in the editor's own data model.
    // The WYSIWYG editor itself can never *create* such a list (its UI only
    // ever produces sequential numbering), so this only bites content
    // imported from elsewhere. Flagged rather than hacked around per the
    // round-trip rule.
    const repeatedMarkers = '1. a\n1. b\n1. c\n'
    expect(roundTrip(repeatedMarkers)).toBe('1. a\n2. b\n3. c\n')
  })

  it.skip('GFM table column alignment (:---:) is not implemented — v1 scope only covers unaligned pipe tables, per design.md §4 ("no merged cells" is the only stated table limitation; alignment was descoped for the spike, not because it cannot round-trip)', () => {
    const alignedTable = '| a | b |\n| :--- | ---: |\n| 1 | 2 |\n'
    expect(roundTrip(alignedTable)).toBe(alignedTable)
  })

  it('loose lists (blank line before a nested sub-list) tighten up (not byte-identical)', () => {
    // CommonMark's loose/tight list distinction is a rendering hint (whether
    // list item content gets wrapped in <p>), not a structural one — nested
    // lists are recognized by indentation alone, blank line or not.
    // ProseMirror/TipTap's list item content is always block-wrapped, so
    // there is nowhere in the document model to record "this item's content
    // was followed by a blank line" — the distinction is destroyed at parse
    // time (see fromMarkdown.ts's parseList/parseListItem) and there's
    // nothing to reconstruct at serialize time. The editor itself can never
    // *produce* a loose list, so this only affects imported content (§13)
    // on its first save. See design.md §4's "Canonical normalization" table.
    const looseNesting = '- Item 1\n- Item 2\n\n  - Nested A\n  - Nested B\n'
    const tightNesting = '- Item 1\n- Item 2\n  - Nested A\n  - Nested B\n'
    expect(roundTrip(looseNesting)).toBe(tightNesting)
  })
})
