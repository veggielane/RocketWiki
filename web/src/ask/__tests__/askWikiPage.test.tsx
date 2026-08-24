import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import type { Operation } from 'urql'
import { AskWikiPage } from '../../pages/AskWikiPage'
import { AskWikiEntryButton } from '../AskWikiEntryButton'
import { AskWikiSearchNudge } from '../AskWikiSearchNudge'
import { markAskWikiNotConfigured, resetAskWikiAvailability } from '../askAvailability'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'
import type { AskWikiQuery } from '../../graphql/generated/graphql'

/**
 * "Ask the wiki" (design.md §9.5) through the real generated document. The
 * load-bearing properties: the full ask flow (pending → answer + sources,
 * citation links on the search-hit anchor contract), all three
 * `unavailable` payload facts as designed UX (never raw errors), the
 * session-level NOT_CONFIGURED collapse of every affordance, and the
 * composer's keyboard contract.
 */

type Payload = AskWikiQuery['askWiki']

const CITATIONS = [
  { pageId: 'p1', title: 'Turbopump overview', headingPath: ['Design', 'Impeller'], anchorId: 'impeller' },
  { pageId: 'p2', title: 'Igniter spec', headingPath: [] as string[], anchorId: 'igniter-spec' },
]

const answered = (answer: string, citations = CITATIONS): { askWiki: Payload } => ({
  askWiki: { answer, citations, unavailable: null },
})
const unavailable = (reason: NonNullable<Payload['unavailable']>): { askWiki: Payload } => ({
  askWiki: { answer: null, citations: [], unavailable: reason },
})

function renderAsk(
  respond: (name: string, op: Operation) => Record<string, unknown> | undefined,
  { route = '/ask' } = {},
) {
  const mock = createMockUrqlClient(respond)
  const utils = render(
    <UrqlProvider value={mock.client}>
      <MemoryRouter initialEntries={[route]}>
        <AskWikiPage />
      </MemoryRouter>
    </UrqlProvider>,
  )
  return { mock, ...utils }
}

function askQuestion(text: string) {
  const field = screen.getByLabelText('Ask a question')
  fireEvent.change(field, { target: { value: text } })
  fireEvent.keyDown(field, { key: 'Enter' })
  return field
}

beforeEach(() => {
  resetAskWikiAvailability()
})

describe('AskWikiPage — ask flow', () => {
  it('pending → answer with citation links → sources list; the input clears', async () => {
    const { mock } = renderAsk(() => answered('Titanium impeller [S1], igniter per spec [S2].'))
    const field = askQuestion('What is the impeller made of?')

    // Multi-second latency is the norm (§9.5, non-streaming). The visible
    // pending row says so; the announcement rides the page's persistent
    // live region (mounted before the wait — a region inserted with its
    // content is not reliably announced).
    expect(screen.getByText(/several seconds/)).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Looking for an answer…')

    expect(await screen.findByText(/Titanium impeller/)).toBeInTheDocument()
    // The question stays on screen; the composer emptied for the next one.
    expect(screen.getByText('What is the impeller made of?')).toBeInTheDocument()
    expect(field).toHaveValue('')

    // Inline markers became superscript links on the search-hit anchor contract.
    expect(screen.getByRole('link', { name: 'Source 1: Turbopump overview' })).toHaveAttribute(
      'href',
      '/pages/p1#impeller',
    )
    expect(screen.getByRole('link', { name: 'Source 2: Igniter spec' })).toHaveAttribute('href', '/pages/p2#igniter-spec')

    // Sources list: title + heading breadcrumb, same links.
    expect(screen.getByText('Sources')).toBeInTheDocument()
    expect(screen.getByText('Turbopump overview')).toBeInTheDocument()
    expect(screen.getByText('Design › Impeller')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /S1 Turbopump overview/ })).toHaveAttribute('href', '/pages/p1#impeller')

    // Exactly one wire operation, carrying the trimmed question.
    const ops = mock.operations.filter((o) => o.name === 'AskWiki')
    expect(ops).toHaveLength(1)
    expect(ops[0].variables).toEqual({ question: 'What is the impeller made of?' })
  })

  it('the live region is mounted before any ask and settles to "Answer ready." after one', async () => {
    renderAsk(() => answered('Titanium.'))
    // Present-and-empty from first render: announcement only works for
    // mutations inside an already-existing region.
    expect(screen.getByRole('status')).toBeEmptyDOMElement()
    askQuestion('What is the impeller made of?')
    expect(screen.getByRole('status')).toHaveTextContent('Looking for an answer…')
    await screen.findByText('Titanium.')
    expect(screen.getByRole('status')).toHaveTextContent('Answer ready.')
  })

  it('has no axe violations in the answered state (citations + sources)', async () => {
    renderAsk(() => answered('Titanium impeller [S1], igniter per spec [S2].'))
    askQuestion('What is the impeller made of?')
    await screen.findByText(/Titanium impeller/)
    await expectNoAxeViolations()
  })

  it('a marker without a matching citation renders as plain text, not a dead link', async () => {
    renderAsk(() => answered('Real [S1], fabricated [S9].', [CITATIONS[0]]))
    askQuestion('q')
    expect(await screen.findByText(/fabricated \[S9\]\./)).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: /^Source/ })).toHaveLength(1)
  })

  it('accumulates a session-local transcript of Q&A pairs', async () => {
    renderAsk((_name, op) =>
      answered(`Answer to: ${(op.variables as { question: string }).question}`, [CITATIONS[0]]),
    )
    askQuestion('first question')
    expect(await screen.findByText('Answer to: first question')).toBeInTheDocument()
    askQuestion('second question')
    expect(await screen.findByText('Answer to: second question')).toBeInTheDocument()
    // Both pairs remain on screen — and the page says the transcript is ephemeral.
    expect(screen.getByText('first question')).toBeInTheDocument()
    expect(screen.getByText('Answer to: first question')).toBeInTheDocument()
    expect(screen.getByText('second question')).toBeInTheDocument()
    expect(screen.getByText(/Nothing here is saved/)).toBeInTheDocument()
  })

  it('Enter submits; Shift+Enter does not (newline stays in the multiline field)', () => {
    const { mock } = renderAsk(() => answered('a'))
    const field = screen.getByLabelText('Ask a question')
    // A real textarea — so Shift+Enter's untouched default is a newline.
    expect(field.tagName).toBe('TEXTAREA')
    fireEvent.change(field, { target: { value: 'multi\nline question' } })
    fireEvent.keyDown(field, { key: 'Enter', shiftKey: true })
    expect(mock.operations).toHaveLength(0)
    expect(field).toHaveValue('multi\nline question')
    fireEvent.keyDown(field, { key: 'Enter' })
    expect(mock.operations.filter((o) => o.name === 'AskWiki')).toHaveLength(1)
  })

  it('an empty or whitespace-only question never hits the wire', () => {
    const { mock } = renderAsk(() => answered('a'))
    const field = screen.getByLabelText('Ask a question')
    fireEvent.keyDown(field, { key: 'Enter' })
    fireEvent.change(field, { target: { value: '   \n ' } })
    fireEvent.keyDown(field, { key: 'Enter' })
    expect(mock.operations).toHaveLength(0)
    // The persistent live region is always mounted — "nothing pending" is
    // it staying empty, not it being absent.
    expect(screen.getByRole('status')).toBeEmptyDOMElement()
  })

  it('prefills the question from ?q= (search handoff) without auto-submitting', () => {
    const { mock } = renderAsk(() => answered('a'), { route: '/ask?q=turbopump%20seals' })
    expect(screen.getByLabelText('Ask a question')).toHaveValue('turbopump seals')
    // Prefill only — an ask is a multi-second model call, never a navigation side effect.
    expect(mock.operations).toHaveLength(0)
  })
})

describe('AskWikiPage — unavailable payload facts (§18 degradation, never raw errors)', () => {
  it('NO_RESULTS: honest copy plus a keyword-search escape hatch', async () => {
    renderAsk(() => unavailable('NO_RESULTS'))
    askQuestion('undocumented thing?')
    expect(await screen.findByText('Nothing in the wiki you can view answers this.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'keyword search' })).toHaveAttribute(
      'href',
      '/search?q=undocumented%20thing%3F',
    )
    // The feature exists — the composer stays for the next question.
    expect(screen.getByLabelText('Ask a question')).toBeInTheDocument()
  })

  it('UNREACHABLE: legible retryable state; retry re-runs the same question', async () => {
    let reachable = false
    const { mock } = renderAsk(() => (reachable ? answered('Recovered answer [S1].') : unavailable('UNREACHABLE')))
    askQuestion('flaky?')
    expect(await screen.findByText(/assistant endpoint couldn't be reached/)).toBeInTheDocument()
    reachable = true
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(await screen.findByText(/Recovered answer/)).toBeInTheDocument()
    const ops = mock.operations.filter((o) => o.name === 'AskWiki')
    expect(ops).toHaveLength(2)
    expect(ops[1].variables).toEqual({ question: 'flaky?' })
  })

  it('NOT_CONFIGURED: feature-absent copy replaces the composer, and every affordance collapses for the session', async () => {
    // Entry points rendered alongside the page — the same store must
    // collapse all of them from one NOT_CONFIGURED answer (the attempt is
    // the probe; there is no assistant status query to gate on).
    const mock = createMockUrqlClient(() => unavailable('NOT_CONFIGURED'))
    render(
      <UrqlProvider value={mock.client}>
        <MemoryRouter initialEntries={['/ask']}>
          <AskWikiEntryButton />
          <AskWikiSearchNudge query="anything" />
          <AskWikiPage />
        </MemoryRouter>
      </UrqlProvider>,
    )
    // Two affordances up front: the app-bar button and the search nudge's
    // link both answer to the accessible name "Ask the wiki".
    expect(screen.getAllByRole('link', { name: 'Ask the wiki' })).toHaveLength(2)
    expect(screen.getByText(/Can't find it\?/)).toBeInTheDocument()

    askQuestion('is anyone there?')
    expect(await screen.findByText(/isn't available on this instance/)).toBeInTheDocument()
    // The transcript keeps the asked question with honest per-entry copy…
    expect(screen.getByText("The wiki assistant isn't configured on this instance.")).toBeInTheDocument()
    // …the composer is gone…
    expect(screen.queryByLabelText('Ask a question')).not.toBeInTheDocument()
    // …and both entry points disappeared.
    await waitFor(() => {
      expect(screen.queryAllByRole('link', { name: 'Ask the wiki' })).toHaveLength(0)
      expect(screen.queryByText(/Can't find it\?/)).not.toBeInTheDocument()
    })
  })

  it('revisiting /ask in a session that already learned NOT_CONFIGURED shows the feature-absent state immediately', () => {
    markAskWikiNotConfigured()
    const { mock } = renderAsk(() => unavailable('NOT_CONFIGURED'))
    expect(screen.getByText(/isn't available on this instance/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Ask a question')).not.toBeInTheDocument()
    expect(mock.operations).toHaveLength(0)
  })
})
