import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor } from '../../editor/RichTextEditor'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import type { GitLabUnavailableReason } from '../../graphql/generated/graphql'

/**
 * The ` ```gitlab-issues ` embed (design.md §18) through the real read-only
 * editor: a compact table of live issues, the fence's key=value filter
 * serialized verbatim onto the wire, and the same degradation vocabulary as
 * every other GitLab surface.
 */

const ISSUES_MD = '```gitlab-issues\nproject=propulsion/turbopump\nstate=opened\nlabels=bug,ops\nfirst=5\n```\n'

const ISSUES = [
  {
    project: 'propulsion/turbopump',
    iid: 57,
    title: 'Pump cavitation at high thrust',
    state: 'opened',
    labels: ['bug', 'ops'],
    assigneeNames: ['Ada Lovelace'],
    milestone: 'v2',
    updatedAtUtc: '2026-08-01T00:00:00Z',
    webUrl: 'https://gitlab.example.com/propulsion/turbopump/-/issues/57',
  },
  {
    project: 'propulsion/turbopump',
    iid: 61,
    title: 'Seal erosion after 40s burn',
    state: 'closed',
    labels: [],
    assigneeNames: [],
    milestone: null,
    updatedAtUtc: null,
    webUrl: 'https://gitlab.example.com/propulsion/turbopump/-/issues/61',
  },
]

function renderBlock({
  configured = true,
  issues = null as typeof ISSUES | null,
  unavailable = null as { reason: GitLabUnavailableReason; upstreamStatus?: number | null } | null,
  markdown = ISSUES_MD,
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured, baseUrl: configured ? 'https://gitlab.example.com' : null, viewerHasToken: true } }
    if (name === 'GitLabIssues')
      return {
        gitlabIssues: {
          issues,
          unavailable: unavailable ? { upstreamStatus: null, ...unavailable } : null,
        },
      }
    return undefined
  })
  const utils = render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor initialMarkdown={markdown} editable={false} showToolbar={false} />
    </UrqlProvider>,
  )
  return { mock, ...utils }
}

describe('gitlab-issues block — live list (read mode)', () => {
  it('renders a table row per issue: title→webUrl, state, labels, assignees', async () => {
    renderBlock({ issues: ISSUES })
    const table = await screen.findByRole('table')
    const rows = within(table).getAllByRole('row')
    expect(rows).toHaveLength(3) // header + 2 issues

    const first = rows[1]!
    expect(within(first).getByRole('link', { name: '#57 Pump cavitation at high thrust' })).toHaveAttribute(
      'href',
      ISSUES[0]!.webUrl,
    )
    expect(within(first).getByText('open')).toBeInTheDocument()
    expect(within(first).getByText('bug, ops')).toBeInTheDocument()
    expect(within(first).getByText('Ada Lovelace')).toBeInTheDocument()

    const second = rows[2]!
    expect(within(second).getByText('closed')).toBeInTheDocument()
  })

  it('serializes the fence filter verbatim onto the wire (out-of-vocabulary is the server’s to degrade)', async () => {
    const { mock } = renderBlock({
      issues: [],
      markdown: '```gitlab-issues\nproject=142\nstate=banana\nsearch=pump\nmilestone=v2\norderBy=updated_at\nsort=asc\n```\n',
    })
    await screen.findByText('No matching issues.')
    const op = mock.operations.find((o) => o.name === 'GitLabIssues')
    expect(op?.variables).toEqual({
      projectId: '142',
      filter: { state: 'banana', labels: null, search: 'pump', milestone: 'v2', orderBy: 'updated_at', sort: 'asc' },
      first: null,
    })
  })

  it('passes labels as a list and first as an Int', async () => {
    const { mock } = renderBlock({ issues: ISSUES })
    await screen.findByRole('table')
    const op = mock.operations.find((o) => o.name === 'GitLabIssues')
    expect(op?.variables).toEqual({
      projectId: 'propulsion/turbopump',
      filter: { state: 'opened', labels: ['bug', 'ops'], search: null, milestone: null, orderBy: null, sort: null },
      first: 5,
    })
  })
})

describe('gitlab-issues block — degradation (§18)', () => {
  it('unavailable renders the typed placeholder with the reference', async () => {
    const { container } = renderBlock({ unavailable: { reason: 'UNREACHABLE', upstreamStatus: 503 } })
    expect(await screen.findByText(/GitLab issues unavailable/)).toHaveTextContent(/could not be reached/i)
    expect(container.querySelector('.rw-gitlab-placeholder-ref')?.textContent).toBe(
      'project=propulsion/turbopump state=opened labels=bug,ops',
    )
  })

  it('credential reasons link to Settings', async () => {
    renderBlock({ unavailable: { reason: 'INVALID_CREDENTIAL', upstreamStatus: 401 } })
    expect(await screen.findByRole('link', { name: 'Open Settings' })).toHaveAttribute('href', '/settings')
  })

  it('unconfigured instance: placeholder, and no issues fetch is attempted', async () => {
    const { mock } = renderBlock({ configured: false })
    expect(await screen.findByText(/GitLab issues unavailable/)).toHaveTextContent(/not configured/i)
    expect(mock.operations.map((o) => o.name)).not.toContain('GitLabIssues')
  })

  it('a fence with no project shows the incomplete hint instead of fetching', async () => {
    const { mock } = renderBlock({ markdown: '```gitlab-issues\nstate=opened\n```\n' })
    expect(await screen.findByText(/Incomplete gitlab-issues reference — missing project/)).toBeInTheDocument()
    expect(mock.operations.map((o) => o.name)).not.toContain('GitLabIssues')
  })
})
