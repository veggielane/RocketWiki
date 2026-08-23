import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor } from '../../editor/RichTextEditor'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import type { GitLabUnavailableReason } from '../../graphql/generated/graphql'

/**
 * The gitlabIssueLink chip (design.md §18), exercised through the real
 * read-only editor — the same render path as page view (one-renderer rule).
 * The mock client stands in for the API proxy; the component never talks to
 * GitLab (there is no URL here but /graphql to talk to).
 */

const ISSUE_MD = 'Tracked in [Fix pump cavitation](gitlab-issue://propulsion/turbopump/57).\n'

const OPEN_ISSUE = {
  project: 'propulsion/turbopump',
  iid: 57,
  title: 'Pump cavitation at high thrust',
  state: 'opened',
  labels: ['bug'],
  webUrl: 'https://gitlab.example.com/propulsion/turbopump/-/issues/57',
}

function renderChip({
  configured = true,
  issue = null as typeof OPEN_ISSUE | null,
  unavailable = null as { reason: GitLabUnavailableReason; upstreamStatus?: number | null } | null,
  editable = false,
  markdown = ISSUE_MD,
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured, baseUrl: configured ? 'https://gitlab.example.com' : null, viewerHasToken: true } }
    if (name === 'GitLabIssue')
      return {
        gitlabIssue: {
          issue,
          unavailable: unavailable ? { upstreamStatus: null, ...unavailable } : null,
        },
      }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor initialMarkdown={markdown} editable={editable} showToolbar={false} />
    </UrqlProvider>,
  )
  return mock
}

describe('gitlab issue chip — live state (read mode)', () => {
  it('renders the author’s link text as a chip linking to webUrl with an open badge', async () => {
    const mock = renderChip({ issue: OPEN_ISSUE })
    const link = await screen.findByRole('link', { name: /Fix pump cavitation/ })
    expect(link).toHaveAttribute('href', OPEN_ISSUE.webUrl)
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
    expect(link).toHaveClass('rw-gitlab-issue-chip')
    // Badge shows "open" for GitLab's "opened"; the live title rides on the tooltip.
    expect(link).toHaveTextContent('open')
    expect(link).toHaveAttribute('title', OPEN_ISSUE.title)
    // The query went through the API proxy with the parsed reference.
    const op = mock.operations.find((o) => o.name === 'GitLabIssue')
    expect(op?.variables).toEqual({ projectId: 'propulsion/turbopump', iid: 57 })
  })

  it('renders a closed badge for closed issues', async () => {
    renderChip({ issue: { ...OPEN_ISSUE, state: 'closed' } })
    const link = await screen.findByRole('link', { name: /Fix pump cavitation/ })
    expect(link).toHaveTextContent('closed')
  })
})

describe('gitlab issue chip — degradation vocabulary (§18)', () => {
  const cases: [reason: GitLabUnavailableReason, expectedCopy: RegExp, mentionsSettings: boolean][] = [
    ['NOT_CONFIGURED', /not configured/i, false],
    ['NO_CREDENTIAL', /no gitlab token saved/i, true],
    ['INVALID_CREDENTIAL', /rejected your token/i, true],
    ['NOT_FOUND', /no such item visible to you/i, false],
    ['UNREACHABLE', /could not be reached/i, false],
  ]

  for (const [reason, expectedCopy, mentionsSettings] of cases) {
    it(`${reason}: plain link text with a subtle marker whose text explains`, async () => {
      renderChip({ unavailable: { reason, upstreamStatus: null } })
      // The author's text renders plainly — never a chip, never a link out.
      expect(await screen.findByText('Fix pump cavitation')).toBeInTheDocument()
      expect(screen.queryByRole('link', { name: /Fix pump cavitation/ })).not.toBeInTheDocument()
      const explanation = screen.getByText(/GitLab issue link —/)
      expect(explanation).toHaveTextContent(expectedCopy)
      if (mentionsSettings) {
        // The credential reasons point at Settings — that page is the fix.
        expect(explanation).toHaveTextContent(/Settings/)
      } else {
        expect(explanation).not.toHaveTextContent(/Settings/)
      }
    })
  }

  it('when the instance is unconfigured, no issue fetch is even attempted (fail-closed affordances)', async () => {
    const mock = renderChip({ configured: false })
    expect(await screen.findByText('Fix pump cavitation')).toBeInTheDocument()
    expect(screen.getByText(/GitLab issue link —/)).toHaveTextContent(/not configured/i)
    expect(mock.operations.map((o) => o.name)).not.toContain('GitLabIssue')
  })
})

describe('gitlab issue chip — edit mode', () => {
  it('renders as a plain styled span and fetches nothing at all', async () => {
    const mock = renderChip({ editable: true })
    expect(await screen.findByText('Fix pump cavitation')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Fix pump cavitation/ })).not.toBeInTheDocument()
    expect(mock.operations).toHaveLength(0)
  })
})
