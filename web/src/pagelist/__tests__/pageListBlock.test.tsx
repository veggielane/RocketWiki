import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor } from '../../editor/RichTextEditor'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The ` ```page-list ` widget (design.md §22) through the real read-only
 * editor — same shape as the GitLab issue-list suite: the component runs its
 * real generated query document, and the test controls only the wire
 * response.
 *
 * The states are asserted in the order the widget resolves them: query errors
 * (authored content, so they must be visible), loading, empty, rows.
 */

const MARKING = (level: string, levelName: string) => ({
  level,
  levelName,
  eyesOnly: [],
  prefix: 'UK',
  label: `UK ${levelName}`,
})

const ROWS = [
  {
    cursor: 'c1',
    node: { page: { id: 'page-1', title: 'Pad abort safety case', spaceKey: 'ENG', marking: MARKING('SECRET', 'SECRET') } },
  },
  {
    cursor: 'c2',
    node: {
      page: { id: 'page-2', title: 'Range safety checklist', spaceKey: 'OPS', marking: MARKING('OFFICIAL', 'OFFICIAL') },
    },
  },
]

const LIST_MD = '```page-list\nquery = label = "safety" AND space IN ("ENG", "OPS")\nlimit = 20\n```\n'

function renderBlock({
  payload = undefined as Record<string, unknown> | undefined,
  markdown = LIST_MD,
} = {}) {
  const mock = createMockUrqlClient((name) => (name === 'PageListQuery' ? { pageQuery: payload } : undefined))
  const utils = render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor initialMarkdown={markdown} editable={false} showToolbar={false} />
    </UrqlProvider>,
  )
  return { mock, ...utils }
}

const ok = (over: Record<string, unknown> = {}) => ({
  aggregateMarking: null,
  totalCount: 2,
  errors: [],
  edges: ROWS,
  pageInfo: { hasNextPage: false, endCursor: null },
  ...over,
})

describe('page-list widget — rows', () => {
  it('renders a real list, one row per page: title link, space, classification badge', async () => {
    renderBlock({ payload: ok() })
    const list = await screen.findByRole('list')
    const items = within(list).getAllByRole('listitem')
    expect(items).toHaveLength(2)

    const first = items[0]!
    expect(within(first).getByRole('link', { name: 'Pad abort safety case' })).toHaveAttribute('href', '/pages/page-1')
    expect(within(first).getByText('ENG')).toBeInTheDocument()
    expect(within(first).getByText('SECRET')).toBeInTheDocument()

    const second = items[1]!
    expect(within(second).getByRole('link', { name: 'Range safety checklist' })).toHaveAttribute('href', '/pages/page-2')
    expect(within(second).getByText('OFFICIAL')).toBeInTheDocument()
  })

  it('sends the fence’s query and limit verbatim — the client never rewrites RQL', async () => {
    const { mock } = renderBlock({ payload: ok() })
    await screen.findByRole('list')
    expect(mock.operations.find((o) => o.name === 'PageListQuery')?.variables).toEqual({
      query: 'label = "safety" AND space IN ("ENG", "OPS")',
      first: 20,
    })
  })

  it('an absent limit sends no first — the server picks the page size', async () => {
    const { mock } = renderBlock({ payload: ok(), markdown: '```page-list\nquery = label = "safety"\n```\n' })
    await screen.findByRole('list')
    expect(mock.operations.find((o) => o.name === 'PageListQuery')?.variables).toEqual({
      query: 'label = "safety"',
      first: null,
    })
  })

  it('states the count when nothing is truncated', async () => {
    renderBlock({ payload: ok() })
    expect(await screen.findByText('2 matching pages.')).toBeInTheDocument()
  })

  // §6.7: `totalCount` is permission-filtered, so both numbers are honest and
  // the sentence is about the fence's `limit`. Nothing here may suggest that
  // results were withheld from this reader.
  it('a truncated list says "the first N", about the limit and never about permissions', async () => {
    renderBlock({ payload: ok({ totalCount: 40 }) })
    expect(await screen.findByText('Showing the first 2 of 40 matching pages.')).toBeInTheDocument()
    expect(screen.queryByText(/hidden|restricted|not visible|cannot see|permission/i)).toBeNull()
  })

  it('renders the aggregate marking with a scope note covering the whole match set (§21.13)', async () => {
    renderBlock({ payload: ok({ aggregateMarking: { level: 'SECRET', label: 'UK SECRET [UK EYES ONLY]' } }) })
    expect(await screen.findByText('UK SECRET [UK EYES ONLY]')).toBeInTheDocument()
    expect(
      screen.getByText('Covers every page matching this query, including any beyond the number listed.'),
    ).toBeInTheDocument()
  })

  it('a null aggregate renders no banner at all', async () => {
    const { container } = renderBlock({ payload: ok() })
    await screen.findByRole('list')
    expect(container.querySelector('[data-aggregate-marking]')).toBeNull()
  })

  it('axe passes over the rendered widget', async () => {
    renderBlock({ payload: ok({ aggregateMarking: { level: 'SECRET', label: 'UK SECRET' }, totalCount: 40 }) })
    await screen.findByRole('list')
    await expectNoAxeViolations()
  })
})

describe('page-list widget — query errors (§22.6)', () => {
  it('renders the server’s positioned errors with the offending text, and no rows', async () => {
    renderBlock({
      payload: ok({
        // §22.3: `marking` is refused by name, with its own code — never
        // silently ignored, never reported as an unknown field.
        errors: [{ code: 'NOT_QUERYABLE_FIELD', message: 'Field "marking" is not queryable.', offset: 16, length: 7 }],
        edges: [],
        totalCount: 0,
        aggregateMarking: null,
      }),
      markdown: '```page-list\nquery = label = "a" AND marking = SECRET\n```\n',
    })
    expect(await screen.findByText(/can't run as written/)).toBeInTheDocument()
    expect(screen.getByText('Field "marking" is not queryable.')).toBeInTheDocument()
    expect(screen.getByText('marking')).toBeInTheDocument()
    // No rows: a refused query lists nothing, rather than listing nothing
    // AND looking like an empty result.
    expect(screen.queryByRole('link')).toBeNull()
    expect(screen.queryByText(/matching page/)).toBeNull()
  })

  it('an unparseable fence keeps the source visible in read mode, so content is never invisible', async () => {
    const { container } = renderBlock({ markdown: '```page-list\nlimit = 5\n```\n' })
    expect(await screen.findByText(/Incomplete page-list reference — missing query/)).toBeInTheDocument()
    expect(container.querySelector('.rw-fence-block')).toHaveAttribute('data-render-state', 'error')
  })

  it('a fence with no query never reaches the API', async () => {
    const { mock } = renderBlock({ markdown: '```page-list\nlimit = 5\n```\n' })
    await screen.findByText(/Incomplete page-list reference/)
    expect(mock.operations.map((o) => o.name)).not.toContain('PageListQuery')
  })
})

describe('page-list widget — loading and empty', () => {
  it('shows a loading state before the payload arrives', async () => {
    renderBlock({ payload: undefined })
    expect(await screen.findByText('Loading matching pages…')).toBeInTheDocument()
  })

  it('an empty result says so, and says nothing about why', async () => {
    renderBlock({ payload: ok({ edges: [], totalCount: 0 }) })
    expect(await screen.findByText('No pages match this query, so this list is empty.')).toBeInTheDocument()
    expect(screen.queryByText(/hidden|restricted|permission/i)).toBeNull()
  })
})
