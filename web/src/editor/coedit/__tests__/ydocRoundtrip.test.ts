import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import { getSchema } from '@tiptap/core'
import { yDocToProsemirrorJSON } from '@tiptap/y-tiptap'
import { editorExtensions } from '../../extensions'
import { markdownToJson } from '../../markdown/fromMarkdown'
import { jsonToMarkdown } from '../../markdown/toMarkdown'
import { seedDocFromMarkdown } from '../seedDoc'

/**
 * The round-trip rule (design.md §4) applied to the CO-EDITING path: the
 * seeder converts saved Markdown into a Y.Doc (seedDoc.ts), every joiner
 * renders that Y.Doc back into ProseMirror JSON, and the next save
 * serializes it to Markdown. If the Y.Doc leg lost or reshaped anything,
 * the first collaborative save would rewrite content nobody touched — so
 * Markdown → JSON → Y.Doc → JSON → Markdown must stay byte-identical over
 * the same feature set the direct round-trip suite pins (a representative
 * cross-section here; roundtrip.test.ts remains the exhaustive corpus).
 */
describe('Markdown → Y.Doc → Markdown round trip (co-editing seed fidelity)', () => {
  const cases: [name: string, markdown: string][] = [
    ['headings + paragraph', '# H1\n\n## H2\n\nBody text.\n'],
    ['marks incl. overlap', 'Some **bold**, *italic*, ~~struck~~, `code`, and **a *b* c**.\n'],
    ['nested bullet list', '- one\n  - nested a\n  - nested b\n- two\n'],
    ['ordered list custom start', '5. five\n6. six\n'],
    ['task list', '- [ ] open item\n- [x] done item\n'],
    ['blockquote multiple paragraphs', '> first\n>\n> second\n'],
    ['pipe table', '| Name | Age |\n| --- | --- |\n| Ada | 36 |\n| Alan | 41 |\n'],
    ['fenced code block with language', '```js\nconst a = 1;\nconsole.log(a);\n```\n'],
    ['mermaid fence (schema-neutral diagram)', '```mermaid\ngraph TD\n  A --> B\n```\n'],
    ['callout directive', ':::warning\nMind the gap.\n:::\n'],
    ['page link + user mention + attachment image', 'See [Runbook](page://11111111-1111-1111-1111-111111111111) by @[Ada Lovelace](user://22222222-2222-2222-2222-222222222222).\n\n![diagram](attachment://33333333-3333-3333-3333-333333333333)\n'],
    ['emoji stays literal text', 'Ship it :rocket: today.\n'],
  ]

  it.each(cases)('%s', (_name, markdown) => {
    const doc = new Y.Doc()
    seedDocFromMarkdown(doc, markdown, 'test')
    const rendered = yDocToProsemirrorJSON(doc, 'default')
    expect(jsonToMarkdown(rendered)).toBe(markdown)
    doc.destroy()
  })

  it('the seeded fragment is schema-valid against the one editor schema (not just JSON-shaped)', () => {
    const markdown = '# Title\n\n- [x] done\n\n:::note\nA callout.\n:::\n'
    const doc = new Y.Doc()
    seedDocFromMarkdown(doc, markdown, 'test')
    const schema = getSchema(editorExtensions)
    // Throws if the node tree violates the schema.
    const node = schema.nodeFromJSON(yDocToProsemirrorJSON(doc, 'default'))
    node.check()
    expect(jsonToMarkdown(node.toJSON())).toBe(markdown)
    expect(jsonToMarkdown(markdownToJson(markdown))).toBe(markdown) // and the direct path agrees
    doc.destroy()
  })
})
