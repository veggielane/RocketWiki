import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor } from '../../editor/RichTextEditor'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import type { GitLabContentIssue, GitLabUnavailableReason } from '../../graphql/generated/graphql'

/**
 * The ` ```gitlab-file ` embed (design.md §18), exercised through the real
 * read-only editor. The critical property pinned here: file content is
 * third-party text and renders exclusively as TEXT — a file full of HTML
 * must never become elements.
 */

const FILE_MD = '```gitlab-file\nproject=propulsion/turbopump\npath=docs/spec.md\nref=main\n```\n'

const BASE_FILE = {
  project: 'propulsion/turbopump',
  filePath: 'docs/spec.md',
  ref: 'main',
  fileName: 'spec.md',
  sizeBytes: 2048,
  lastCommitId: 'abc123',
  content: '# Spec\n\nplain text body\n',
  contentIssue: null as GitLabContentIssue | null,
}

function renderBlock({
  configured = true,
  file = null as typeof BASE_FILE | null,
  unavailable = null as { reason: GitLabUnavailableReason; upstreamStatus?: number | null } | null,
  markdown = FILE_MD,
  editable = false,
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured, baseUrl: configured ? 'https://gitlab.example.com' : null, viewerHasToken: true } }
    if (name === 'GitLabFile')
      return {
        gitlabFile: {
          file,
          unavailable: unavailable ? { upstreamStatus: null, ...unavailable } : null,
        },
      }
    return undefined
  })
  const utils = render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor initialMarkdown={markdown} editable={editable} showToolbar={false} />
    </UrqlProvider>,
  )
  return { mock, ...utils }
}

describe('gitlab-file block — live content (read mode)', () => {
  it('shows header (fileName, ref, size, link out) and the content in a pre', async () => {
    const { mock, container } = renderBlock({ file: BASE_FILE })
    expect(await screen.findByText('spec.md')).toBeInTheDocument()
    // 2048 bytes through the one shared decimal formatter (attachments/formatBytes.ts).
    expect(screen.getByText(/main · 2\.0 KB/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open in GitLab' })).toHaveAttribute(
      'href',
      'https://gitlab.example.com/propulsion/turbopump/-/blob/main/docs/spec.md',
    )
    expect(container.querySelector('.rw-gitlab-file-content')?.textContent).toContain('plain text body')
    // Fetch went through the API proxy with the parsed reference.
    const op = mock.operations.find((o) => o.name === 'GitLabFile')
    expect(op?.variables).toEqual({ projectId: 'propulsion/turbopump', path: 'docs/spec.md', ref: 'main' })
  })

  it('renders file content strictly as text — HTML in the file never becomes elements', async () => {
    const hostile = '<script>window.pwned = true</script><img src=x onerror="window.pwned = true"><b>bold?</b>'
    const { container } = renderBlock({ file: { ...BASE_FILE, content: hostile } })
    await screen.findByText('spec.md')
    const pre = container.querySelector('.rw-gitlab-file-content')
    // The markup is *visible as text*…
    expect(pre?.textContent).toContain('<script>')
    expect(pre?.textContent).toContain('onerror')
    // …and exists as exactly zero elements, anywhere in the document.
    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('b')).toBeNull()
  })

  it('numeric project ids get no link out (no stable web URL to build) but everything else renders', async () => {
    renderBlock({
      markdown: '```gitlab-file\nproject=142\npath=README.md\n```\n',
      file: { ...BASE_FILE, project: '142', filePath: 'README.md', fileName: 'README.md', ref: null as unknown as string },
    })
    expect(await screen.findByText('README.md')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Open in GitLab' })).not.toBeInTheDocument()
    // Unspecified ref surfaces as HEAD, the server default.
    expect(screen.getByText(/HEAD ·/)).toBeInTheDocument()
  })
})

describe('gitlab-file block — content withheld (metadata still present)', () => {
  it('TOO_LARGE renders the header plus a withheld note, no content pre', async () => {
    const { container } = renderBlock({
      file: { ...BASE_FILE, content: null as unknown as string, contentIssue: 'TOO_LARGE', sizeBytes: 5 * 1024 * 1024 },
    })
    expect(await screen.findByText('spec.md')).toBeInTheDocument()
    expect(screen.getByText('Content withheld: too large to embed.')).toBeInTheDocument()
    expect(screen.getByText(/5\.2 MB/)).toBeInTheDocument()
    expect(container.querySelector('.rw-gitlab-file-content')).toBeNull()
  })

  it('NOT_TEXT renders the header plus a withheld note', async () => {
    renderBlock({ file: { ...BASE_FILE, content: null as unknown as string, contentIssue: 'NOT_TEXT' } })
    expect(await screen.findByText('Content withheld: not a text file.')).toBeInTheDocument()
    expect(screen.getByText('spec.md')).toBeInTheDocument()
  })
})

describe('gitlab-file block — degradation (§18: typed placeholder WITH the reference)', () => {
  it('unavailable renders the reason and the reference (already the page’s own Markdown)', async () => {
    renderBlock({ unavailable: { reason: 'NOT_FOUND' } })
    expect(await screen.findByText(/GitLab file unavailable/)).toHaveTextContent(/no such item visible to you/i)
    expect(screen.getByText('propulsion/turbopump:docs/spec.md@main')).toBeInTheDocument()
  })

  it('credential reasons link to Settings', async () => {
    renderBlock({ unavailable: { reason: 'NO_CREDENTIAL' } })
    expect(await screen.findByRole('link', { name: 'Open Settings' })).toHaveAttribute('href', '/settings')
  })

  it('unconfigured instance: placeholder without ever fetching the file', async () => {
    const { mock } = renderBlock({ configured: false })
    expect(await screen.findByText(/GitLab file unavailable/)).toHaveTextContent(/not configured/i)
    expect(screen.getByText('propulsion/turbopump:docs/spec.md@main')).toBeInTheDocument()
    expect(mock.operations.map((o) => o.name)).not.toContain('GitLabFile')
  })

  it('a fence missing required keys shows the incomplete hint and (read mode) reveals the source', async () => {
    const { container } = renderBlock({ markdown: '```gitlab-file\nref=main\n```\n' })
    expect(await screen.findByText(/Incomplete gitlab-file reference — missing project, path/)).toBeInTheDocument()
    expect(container.querySelector('.rw-gitlab-block')).toHaveAttribute('data-render-state', 'error')
  })
})

describe('no dangerouslySetInnerHTML anywhere in the GitLab surfaces', () => {
  it('static sweep over src/gitlab and the gitlab editor files', () => {
    // vitest's cwd is web/ (where vite.config.ts lives).
    const roots = [
      join(process.cwd(), 'src', 'gitlab'),
      join(process.cwd(), 'src', 'editor', 'gitlab'),
      join(process.cwd(), 'src', 'editor', 'marks'),
    ]
    const offenders: string[] = []
    for (const root of roots) {
      for (const entry of readdirSync(root, { withFileTypes: true })) {
        if (!entry.isFile() || !/\.(ts|tsx)$/.test(entry.name)) continue
        const text = readFileSync(join(root, entry.name), 'utf8')
        // Matches actual usage (JSX attribute / object property), not the
        // doc comments that state this rule.
        if (/dangerouslySetInnerHTML\s*[=:]/.test(text)) offenders.push(entry.name)
      }
    }
    expect(offenders).toEqual([])
  })
})
