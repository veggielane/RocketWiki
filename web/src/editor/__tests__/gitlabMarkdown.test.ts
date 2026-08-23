import { describe, expect, it } from 'vitest'
import { markdownToJson } from '../markdown/fromMarkdown'
import { jsonToMarkdown } from '../markdown/toMarkdown'

/**
 * Direction-specific assertions for the three GitLab content forms
 * (design.md §18 — the byte-identity round trips live in roundtrip.test.ts).
 * What matters here is the *shape*: `gitlab-issue://` links become their own
 * mark with raw-string attrs, while both fences stay ordinary codeBlocks —
 * the serializer pipeline has zero GitLab-fence-specific code, exactly like
 * mermaid.
 */

describe('gitlab-issue:// links parse as gitlabIssueLink marks', () => {
  it('markdown -> mark with project/iid split at the final numeric segment', () => {
    const doc = markdownToJson('See [the pump issue](gitlab-issue://propulsion/turbopump/57).\n')
    expect(doc.content).toEqual([
      {
        type: 'paragraph',
        content: [
          { type: 'text', text: 'See ' },
          {
            type: 'text',
            text: 'the pump issue',
            marks: [{ type: 'gitlabIssueLink', attrs: { project: 'propulsion/turbopump', iid: '57' } }],
          },
          { type: 'text', text: '.' },
        ],
      },
    ])
  })

  it('numeric project ids parse too', () => {
    const doc = markdownToJson('[x](gitlab-issue://142/57)\n')
    expect(doc.content?.[0]?.content?.[0]?.marks).toEqual([
      { type: 'gitlabIssueLink', attrs: { project: '142', iid: '57' } },
    ])
  })

  it('iid attrs stay raw strings so leading zeros survive', () => {
    const doc = markdownToJson('[x](gitlab-issue://legacy/007)\n')
    expect(doc.content?.[0]?.content?.[0]?.marks).toEqual([
      { type: 'gitlabIssueLink', attrs: { project: 'legacy', iid: '007' } },
    ])
  })

  it('mark -> markdown emits the scheme form verbatim', () => {
    const markdown = jsonToMarkdown({
      type: 'doc',
      content: [
        {
          type: 'paragraph',
          content: [
            {
              type: 'text',
              text: 'the pump issue',
              marks: [{ type: 'gitlabIssueLink', attrs: { project: 'propulsion/turbopump', iid: '57' } }],
            },
          ],
        },
      ],
    })
    expect(markdown).toBe('[the pump issue](gitlab-issue://propulsion/turbopump/57)\n')
  })

  const malformed: [name: string, href: string][] = [
    ['non-numeric final segment', 'gitlab-issue://proj/not-a-number'],
    ['no project segment', 'gitlab-issue://57'],
    ['trailing slash', 'gitlab-issue://proj/57/'],
    ['empty target', 'gitlab-issue://'],
  ]

  for (const [name, href] of malformed) {
    it(`malformed (${name}) stays an ordinary link mark`, () => {
      const doc = markdownToJson(`[x](${href})\n`)
      const marks = doc.content?.[0]?.content?.[0]?.marks
      expect(marks?.[0]?.type).toBe('link')
      expect(marks?.[0]?.attrs?.href).toBe(href)
    })
  }

  it('a titled gitlab-issue link stays an ordinary link (the scheme form has no title slot)', () => {
    const source = '[x](gitlab-issue://proj/57 "Title")\n'
    const doc = markdownToJson(source)
    expect(doc.content?.[0]?.content?.[0]?.marks?.[0]?.type).toBe('link')
    expect(jsonToMarkdown(doc)).toBe(source)
  })
})

describe('gitlab fences parse as plain code blocks (never a bespoke node)', () => {
  const FILE_BODY = 'project=propulsion/turbopump\npath=docs/spec.md\nref=main'
  const ISSUES_BODY = 'project=142\nstate=opened\nlabels=bug,ops'

  it('markdown -> codeBlock with language gitlab-file, body preserved as text', () => {
    const doc = markdownToJson(`\`\`\`gitlab-file\n${FILE_BODY}\n\`\`\`\n`)
    expect(doc.content).toEqual([
      {
        type: 'codeBlock',
        attrs: { language: 'gitlab-file' },
        content: [{ type: 'text', text: FILE_BODY }],
      },
    ])
  })

  it('markdown -> codeBlock with language gitlab-issues, body preserved as text', () => {
    const doc = markdownToJson(`\`\`\`gitlab-issues\n${ISSUES_BODY}\n\`\`\`\n`)
    expect(doc.content).toEqual([
      {
        type: 'codeBlock',
        attrs: { language: 'gitlab-issues' },
        content: [{ type: 'text', text: ISSUES_BODY }],
      },
    ])
  })

  it('codeBlock -> fence for both languages', () => {
    const markdown = jsonToMarkdown({
      type: 'doc',
      content: [
        { type: 'codeBlock', attrs: { language: 'gitlab-file' }, content: [{ type: 'text', text: FILE_BODY }] },
        { type: 'codeBlock', attrs: { language: 'gitlab-issues' }, content: [{ type: 'text', text: ISSUES_BODY }] },
      ],
    })
    expect(markdown).toBe(
      `\`\`\`gitlab-file\n${FILE_BODY}\n\`\`\`\n\n\`\`\`gitlab-issues\n${ISSUES_BODY}\n\`\`\`\n`,
    )
  })
})
