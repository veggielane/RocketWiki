import { createRef } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { InsertPageListDialog } from '../InsertPageListDialog'
import { RichTextEditor, type RichTextEditorHandle } from '../../RichTextEditor'
import { createMockUrqlClient } from '../../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../../test/axe'

/**
 * The insert dialog for the ` ```page-list ` fence (design.md §22).
 *
 * The behaviours worth pinning are the ones that would silently produce a
 * wrong or unstable fence: what the builder prints, what the AND/OR toggle
 * changes, and — most of all — that the CANONICAL string is what gets stored
 * (§22.6's fixed-point printer is the only reason the fence's round trip
 * survives repeated edits).
 */

const SPACES = [
  { key: 'ENG', name: 'Engineering' },
  { key: 'OPS', name: 'Operations' },
]
const LABELS = ['safety', 'draft', 'review']

type ParseResult = {
  isValid: boolean
  canonical: string | null
  errors: { code: string; message: string; offset: number; length: number }[]
}

/** By default the server echoes the query back as its own canonical form. */
const echo = (query: string): ParseResult => ({ isValid: true, canonical: query, errors: [] })

function mockClient(canonicalize: (query: string) => ParseResult) {
  return createMockUrqlClient((name, op) => {
    if (name === 'PageListVocabulary') return { spaces: SPACES, labels: LABELS }
    if (name === 'ParseRql') return { parseRql: canonicalize(String(op.variables?.query ?? '')) }
    // The toolbar itself asks; no GitLab in these tests.
    if (name === 'GitLabStatus') return { gitlabStatus: { configured: false, baseUrl: null, viewerHasToken: false } }
    return undefined
  })
}

function renderDialog({
  onInsert = vi.fn(),
  canonicalize = echo,
}: { onInsert?: (spec: { query: string; limit?: number }) => void; canonicalize?: (query: string) => ParseResult } = {}) {
  const mock = mockClient(canonicalize)
  const utils = render(
    <UrqlProvider value={mock.client}>
      <InsertPageListDialog open onClose={() => {}} onInsert={onInsert} />
    </UrqlProvider>,
  )
  return { mock, onInsert, ...utils }
}

/** Selects one option in an MUI Autocomplete, by the text the option renders. */
function pick(fieldLabel: string, typed: string, optionText: string | RegExp = typed) {
  const input = screen.getByLabelText(fieldLabel)
  fireEvent.mouseDown(input)
  fireEvent.change(input, { target: { value: typed } })
  fireEvent.click(screen.getByText(optionText))
}

/** The query the dialog last asked the server to validate. */
const lastParsed = (mock: { operations: { name: string; variables: Record<string, unknown> }[] }) =>
  [...mock.operations].reverse().find((o) => o.name === 'ParseRql')?.variables.query

const expectParsed = (mock: Parameters<typeof lastParsed>[0], query: string) =>
  waitFor(() => expect(lastParsed(mock)).toBe(query), { timeout: 3000 })

describe('InsertPageListDialog — builder → RQL', () => {
  it('a label and a space become one AND-joined query', async () => {
    const { mock } = renderDialog()
    pick('Labels', 'safety')
    pick('Spaces', 'ENG', 'ENG — Engineering')
    await expectParsed(mock, 'label = "safety" AND space = "ENG"')
  })

  it('the AND/OR toggle switches between an AND chain and an IN list', async () => {
    const { mock } = renderDialog()
    pick('Labels', 'draft')
    pick('Labels', 'review')
    await expectParsed(mock, 'label = "draft" AND label = "review"')

    fireEvent.click(screen.getByRole('button', { name: 'Any of these labels (OR)' }))
    await expectParsed(mock, 'label IN ("draft", "review")')

    fireEvent.click(screen.getByRole('button', { name: 'All of these labels (AND)' }))
    await expectParsed(mock, 'label = "draft" AND label = "review"')
  })

  it('two spaces become an IN list', async () => {
    const { mock } = renderDialog()
    pick('Spaces', 'ENG', 'ENG — Engineering')
    pick('Spaces', 'OPS', 'OPS — Operations')
    await expectParsed(mock, 'space IN ("ENG", "OPS")')
  })

  it('the default sort adds no ORDER BY; another sort does (§22.2)', async () => {
    const { mock } = renderDialog()
    pick('Labels', 'safety')
    await expectParsed(mock, 'label = "safety"')

    fireEvent.mouseDown(screen.getByLabelText('Sort by'))
    fireEvent.click(await screen.findByRole('option', { name: 'Title A–Z' }))
    await expectParsed(mock, 'label = "safety" ORDER BY title ASC')
  })

  it('nothing selected: no query is validated and Insert stays disabled', () => {
    const { mock } = renderDialog()
    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
    expect(screen.getByText(/an empty filter lists nothing/)).toBeInTheDocument()
    expect(mock.operations.map((o) => o.name)).not.toContain('ParseRql')
  })
})

describe('InsertPageListDialog — what gets stored', () => {
  it('stores the CANONICAL form, not the text that was typed (§22.6)', async () => {
    const onInsert = vi.fn()
    // The server upper-cases keywords and always quotes values.
    renderDialog({ onInsert, canonicalize: () => ({ isValid: true, canonical: 'label = "safety"', errors: [] }) })
    fireEvent.click(screen.getByRole('button', { name: 'Write a query' }))
    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'label = safety' } })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Insert' })).toBeEnabled(), { timeout: 3000 })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))
    expect(onInsert).toHaveBeenCalledWith({ query: 'label = "safety"' })
  })

  it('carries the limit through as a number', async () => {
    const onInsert = vi.fn()
    const { mock } = renderDialog({ onInsert })
    pick('Labels', 'safety')
    fireEvent.change(screen.getByLabelText('Maximum pages to list'), { target: { value: '20' } })
    await expectParsed(mock, 'label = "safety"')

    await waitFor(() => expect(screen.getByRole('button', { name: 'Insert' })).toBeEnabled(), { timeout: 3000 })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))
    expect(onInsert).toHaveBeenCalledWith({ query: 'label = "safety"', limit: 20 })
  })

  it('switching to the query field hands over what the builder printed', () => {
    renderDialog()
    pick('Labels', 'safety')
    fireEvent.click(screen.getByRole('button', { name: 'Write a query' }))
    expect(screen.getByLabelText('Query')).toHaveValue('label = "safety"')
  })
})

describe('InsertPageListDialog — invalid queries', () => {
  const refused = (): ParseResult => ({
    isValid: false,
    canonical: null,
    // §22.3: refused by name, with its own code — not reported as a typo.
    errors: [{ code: 'NOT_QUERYABLE_FIELD', message: 'Field "marking" is not queryable.', offset: 0, length: 7 }],
  })

  it('shows the server’s message verbatim and refuses to insert', async () => {
    const onInsert = vi.fn()
    renderDialog({ onInsert, canonicalize: refused })
    fireEvent.click(screen.getByRole('button', { name: 'Write a query' }))
    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'marking = SECRET' } })

    expect(await screen.findByText('Field "marking" is not queryable.', {}, { timeout: 3000 })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
    expect(onInsert).not.toHaveBeenCalled()
  })

  it('marks the offending span using the error’s offset/length', async () => {
    renderDialog({ canonicalize: refused })
    fireEvent.click(screen.getByRole('button', { name: 'Write a query' }))
    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'marking = SECRET' } })
    await screen.findByText('Field "marking" is not queryable.', {}, { timeout: 3000 })

    // document.body, not the render container: MUI dialogs portal.
    const mark = document.body.querySelector('mark')
    expect(mark?.textContent).toContain('marking')
    // A spoken lead-in as well as the visible underline — decoration alone
    // would put the whole signal in styling (WCAG 1.4.1).
    expect(mark?.textContent).toContain('problem starts:')
  })

  it('an invalid limit blocks insertion on its own', async () => {
    const { mock } = renderDialog()
    pick('Labels', 'safety')
    await expectParsed(mock, 'label = "safety"')
    fireEvent.change(screen.getByLabelText('Maximum pages to list'), { target: { value: 'twenty' } })
    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
  })
})

describe('InsertPageListDialog — accessibility', () => {
  it('axe passes in the open state, builder mode', async () => {
    renderDialog()
    screen.getByRole('dialog')
    await expectNoAxeViolations()
  })

  it('axe passes with an error read-out on screen', async () => {
    renderDialog({
      canonicalize: () => ({
        isValid: false,
        canonical: null,
        errors: [{ code: 'SYNTAX', message: 'Unexpected end of query.', offset: 6, length: 1 }],
      }),
    })
    fireEvent.click(screen.getByRole('button', { name: 'Write a query' }))
    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'label =' } })
    await screen.findByText('Unexpected end of query.', {}, { timeout: 3000 })
    await expectNoAxeViolations()
  })
})

describe('the toolbar writes the fence', () => {
  it('insert → a ```page-list fence carrying the canonical query and limit', async () => {
    const mock = mockClient(() => ({ isValid: true, canonical: 'label = "safety"', errors: [] }))
    const ref = createRef<RichTextEditorHandle>()
    render(
      <UrqlProvider value={mock.client}>
        <RichTextEditor ref={ref} initialMarkdown="" editable showToolbar />
      </UrqlProvider>,
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Insert page list' }))
    fireEvent.click(screen.getByRole('button', { name: 'Write a query' }))
    fireEvent.change(screen.getByLabelText('Query'), { target: { value: 'label = safety' } })
    fireEvent.change(screen.getByLabelText('Maximum pages to list'), { target: { value: '20' } })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Insert' })).toBeEnabled(), { timeout: 3000 })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    // The stored bytes are the contract: a plain fence, canonical query first.
    expect(ref.current?.getMarkdown()).toBe('```page-list\nquery = label = "safety"\nlimit = 20\n```\n')
  })
})
