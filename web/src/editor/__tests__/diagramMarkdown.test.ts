import { describe, expect, it } from 'vitest'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'

/**
 * Direction-specific assertions for the two diagram fence types (the
 * byte-identity round trips live in roundtrip.test.ts). What matters here
 * is the *shape*: a ```mermaid fence is an ordinary codeBlock (rendering is
 * a NodeView concern — the serializer pipeline has zero mermaid-specific
 * code), while ```drawio is the reserved storage form of the
 * drawioDiagram node.
 */

const PAYLOAD = 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4='

describe('mermaid fences parse as plain code blocks', () => {
  it('markdown -> codeBlock with language mermaid, source preserved as text', () => {
    const doc = markdownToJson('```mermaid\ngraph TD\n  A --> B\n```\n')
    expect(doc.content).toEqual([
      {
        type: 'codeBlock',
        attrs: { language: 'mermaid' },
        content: [{ type: 'text', text: 'graph TD\n  A --> B' }],
      },
    ])
  })

  it('codeBlock with language mermaid -> fence', () => {
    const markdown = jsonToMarkdown({
      type: 'doc',
      content: [
        {
          type: 'codeBlock',
          attrs: { language: 'mermaid' },
          content: [{ type: 'text', text: 'graph TD\n  A --> B' }],
        },
      ],
    })
    expect(markdown).toBe('```mermaid\ngraph TD\n  A --> B\n```\n')
  })
})

describe('drawio fences parse as drawioDiagram nodes', () => {
  it('markdown -> drawioDiagram with the fence body as verbatim payload (not a codeBlock)', () => {
    const doc = markdownToJson(`\`\`\`drawio\n${PAYLOAD}\n\`\`\`\n`)
    expect(doc.content).toEqual([{ type: 'drawioDiagram', attrs: { payload: PAYLOAD, alt: '' } }])
  })

  it('drawioDiagram -> fence with the payload emitted verbatim', () => {
    const markdown = jsonToMarkdown({
      type: 'doc',
      content: [{ type: 'drawioDiagram', attrs: { payload: PAYLOAD } }],
    })
    expect(markdown).toBe(`\`\`\`drawio\n${PAYLOAD}\n\`\`\`\n`)
  })

  it('empty payload (freshly inserted node, attrs defaulted) -> empty fence', () => {
    const markdown = jsonToMarkdown({
      type: 'doc',
      content: [{ type: 'drawioDiagram' }],
    })
    expect(markdown).toBe('```drawio\n\n```\n')
  })

  it('a payload that is not base64 still round-trips verbatim — validity is a rendering concern, never a data-loss one', () => {
    const doc = markdownToJson('```drawio\nnot really base64!!\n```\n')
    expect(doc.content).toEqual([{ type: 'drawioDiagram', attrs: { payload: 'not really base64!!', alt: '' } }])
    expect(jsonToMarkdown(doc)).toBe('```drawio\nnot really base64!!\n```\n')
  })

  it('an `alt:` first line parses into the alt attr, payload unchanged', () => {
    const doc = markdownToJson(`\`\`\`drawio\nalt: Feed system overview\n${PAYLOAD}\n\`\`\`\n`)
    expect(doc.content).toEqual([
      { type: 'drawioDiagram', attrs: { payload: PAYLOAD, alt: 'Feed system overview' } },
    ])
  })

  it('a non-empty alt attr serializes as the `alt:` first line; an empty one emits no line at all', () => {
    expect(
      jsonToMarkdown({
        type: 'doc',
        content: [{ type: 'drawioDiagram', attrs: { payload: PAYLOAD, alt: 'Feed system overview' } }],
      }),
    ).toBe(`\`\`\`drawio\nalt: Feed system overview\n${PAYLOAD}\n\`\`\`\n`)
    expect(
      jsonToMarkdown({
        type: 'doc',
        content: [{ type: 'drawioDiagram', attrs: { payload: PAYLOAD, alt: '' } }],
      }),
    ).toBe(`\`\`\`drawio\n${PAYLOAD}\n\`\`\`\n`)
  })

  it('a lone `alt:` line with no newline after it stays payload — no reshaping of hand-written bodies', () => {
    // `alt: x` as the ENTIRE body is ambiguous; treating it as alt would
    // serialize back with a trailing blank payload line and break §4's
    // byte identity, so the parse requires the newline and leaves it alone.
    const doc = markdownToJson('```drawio\nalt: just some text\n```\n')
    expect(doc.content).toEqual([{ type: 'drawioDiagram', attrs: { payload: 'alt: just some text', alt: '' } }])
    expect(jsonToMarkdown(doc)).toBe('```drawio\nalt: just some text\n```\n')
  })
})
