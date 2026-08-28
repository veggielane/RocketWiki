import { describe, expect, it } from 'vitest'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'

/**
 * Shape assertions for the ` ```page-list ` fence (design.md §22 — the
 * byte-identity round trips live in roundtrip.test.ts).
 *
 * The point of this file is a *negative*: there is no page-list node, no
 * page-list attribute, and no page-list branch anywhere in the Markdown
 * pipeline. The fence is an ordinary `codeBlock` whose `language` happens to
 * be reserved, and its body is one text node holding whatever the author
 * typed. That is what makes the round trip, sync inertness (§12) and importer
 * safety (§13) free rather than three more things to get right — and it is
 * why a future refactor that "promotes" this to a real node would be a
 * regression these assertions should catch.
 */

const BODY = 'query = label = "safety" AND space IN ("ENG", "OPS")\nlimit = 20'

describe('page-list fence parses as a plain code block (never a bespoke node)', () => {
  it('markdown -> codeBlock with language page-list, body preserved as text', () => {
    const doc = markdownToJson(`\`\`\`page-list\n${BODY}\n\`\`\`\n`)
    expect(doc.content).toEqual([
      {
        type: 'codeBlock',
        attrs: { language: 'page-list' },
        content: [{ type: 'text', text: BODY }],
      },
    ])
  })

  it('codeBlock -> fence', () => {
    const markdown = jsonToMarkdown({
      type: 'doc',
      content: [{ type: 'codeBlock', attrs: { language: 'page-list' }, content: [{ type: 'text', text: BODY }] }],
    })
    expect(markdown).toBe(`\`\`\`page-list\n${BODY}\n\`\`\`\n`)
  })

  // An unparseable RQL string is authored content (§22.6), and the pipeline
  // has no opinion about it whatsoever — it is text inside a code fence.
  it('a body that is not valid RQL still parses to the same plain shape', () => {
    const junk = 'query = marking = SECRET'
    const doc = markdownToJson(`\`\`\`page-list\n${junk}\n\`\`\`\n`)
    expect(doc.content).toEqual([
      {
        type: 'codeBlock',
        attrs: { language: 'page-list' },
        content: [{ type: 'text', text: junk }],
      },
    ])
  })
})
