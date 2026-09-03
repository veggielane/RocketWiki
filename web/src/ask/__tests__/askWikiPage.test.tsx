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

/**
 * Two sources at different classifications (design.md §21.13's first rule:
 * every individual result carries its own marking), so a test can tell the
 * per-citation badges apart from the answer's aggregate.
 */
const CITATIONS = [
  {
    pageId: 'p1',
    title: 'Turbopump overview',
    headingPath: ['Design', 'Impeller'],
    anchorId: 'impeller',
    marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
  },
  {
    pageId: 'p2',
    title: 'Igniter spec',
    headingPath: [] as string[],
    anchorId: 'igniter-spec',
    marking: {
      level: 'SECRET',
      levelName: 'SECRET',
      eyesOnly: ['GB'],
      ukPrefix: true, selectors: [],
      label: 'UK SECRET NZ EYES ONLY',
    },
  },
] as const

/**
 * The conjunctive form only an aggregate can take (§21.13): distinct source
 * eyes-only sets are LISTED, never unioned or intersected. It is one opaque
 * server-built string here and the SPA must render it byte-for-byte.
 */
const CONJUNCTIVE = { level: 'SECRET', label: 'UK SECRET NZ EYES ONLY, US EYES ONLY' } as const

const answered = (
  answer: string,
  citations: readonly (typeof CITATIONS)[number][] = CITATIONS,
  aggregateMarking: Payload['aggregateMarking'] = { level: 'SECRET', label: 'UK SECRET NZ EYES ONLY' },
): { askWiki: Payload } => ({
  askWiki: { answer, citations: citations as unknown as Payload['citations'], unavailable: null, aggregateMarking },
})
const unavailable = (reason: NonNullable<Payload['unavailable']>): { askWiki: Payload } => ({
  // §21.13: nothing was shown, so there is nothing to mark — the server sends
  // null, not a substitute level.
  askWiki: { answer: null, citations: [], unavailable: reason, aggregateMarking: null },
})

/**
 * The assistant's status is answered for every render, because the page asks
 * for it before anything is typed — that is the point of it being a status
 * query rather than a field on the answer payload. `maxQuestionChars` is a
 * parameter here so the boundary tests can drive the counter from whatever
 * number the field reports, rather than from a constant baked into the SPA.
 */
function renderAsk(
  respond: (name: string, op: Operation) => Record<string, unknown> | undefined,
  { route = '/ask', maxQuestionChars = 2000 as number | null } = {},
) {
  const mock = createMockUrqlClient((name, op) =>
    name === 'AssistantStatus'
      ? { assistantStatus: { configured: true, maxQuestionChars } }
      : respond(name, op),
  )
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
    expect(mock.operations.filter((o) => o.name === 'AskWiki')).toHaveLength(0)
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
    expect(mock.operations.filter((o) => o.name === 'AskWiki')).toHaveLength(0)
    // The persistent live region is always mounted — "nothing pending" is
    // it staying empty, not it being absent.
    expect(screen.getByRole('status')).toBeEmptyDOMElement()
  })

  it('prefills the question from ?q= (search handoff) without auto-submitting', () => {
    const { mock } = renderAsk(() => answered('a'), { route: '/ask?q=turbopump%20seals' })
    expect(screen.getByLabelText('Ask a question')).toHaveValue('turbopump seals')
    // Prefill only — an ask is a multi-second model call, never a navigation side effect.
    expect(mock.operations.filter((o) => o.name === 'AskWiki')).toHaveLength(0)
  })
})

/**
 * design.md §21.13. The failure this whole block defends against: an answer
 * synthesized from a UK SECRET page arriving unmarked, so a cleared reader
 * pastes it somewhere that is not. The model launders the marking off the
 * content; the aggregate puts it back on.
 */
describe('AskWikiPage — the answer carries its aggregate marking', () => {
  const rowText = (row: HTMLElement | undefined) => row?.textContent ?? ''

  it('renders the aggregate above the answer body, so it travels with the text a reader copies', async () => {
    renderAsk(() => answered('Titanium impeller [S1].'))
    askQuestion('what is it made of?')
    const answer = await screen.findByText(/Titanium impeller/)

    const banner = document.querySelector('[data-aggregate-marking="answer-head"]')
    expect(banner?.textContent).toContain('UK SECRET NZ EYES ONLY')
    // Order, not just presence: the marking must precede the prose it marks.
    expect(banner && (banner.compareDocumentPosition(answer) & Node.DOCUMENT_POSITION_FOLLOWING)).toBeTruthy()
  })

  it('repeats the marking at the foot of the answer, as a page view does', async () => {
    renderAsk(() => answered('Titanium.'))
    askQuestion('q')
    await screen.findByText('Titanium.')
    const foot = document.querySelector('[data-aggregate-marking="answer-foot"]')
    expect(foot?.textContent).toContain('UK SECRET NZ EYES ONLY')
    // Named as a repeat for a screen reader, so meeting it twice doesn't read
    // as the answer changing classification part-way down.
    expect(foot?.textContent).toContain('repeated at the end of this answer')
  })

  it('renders a conjunctive aggregate label verbatim — the SPA composes nothing (§21.1)', async () => {
    // `NZ EYES ONLY, US EYES ONLY` is a shape the per-page marking model
    // cannot express: a union would widen it and an intersection would empty
    // it. Byte-for-byte is the assertion.
    renderAsk(() => answered('Both sources agree [S1][S2].', CITATIONS, CONJUNCTIVE))
    askQuestion('q')
    await screen.findByText(/Both sources agree/)
    expect(screen.getAllByText('UK SECRET NZ EYES ONLY, US EYES ONLY').length).toBeGreaterThan(0)
  })

  it('announces the marking with the answer, not only in the banner', async () => {
    renderAsk(() => answered('Titanium.'))
    askQuestion('q')
    await screen.findByText('Titanium.')
    // A screen-reader user who jumps to the new text would otherwise never
    // meet the banner. Verbatim label after a fixed lead-in.
    expect(screen.getByRole('status')).toHaveTextContent('Answer ready. Protective marking: UK SECRET NZ EYES ONLY')
  })

  it('badges each citation with its OWN source marking, so the sensitive source is visible', async () => {
    renderAsk(() => answered('The impeller [S1], the igniter [S2].'))
    askQuestion('q')
    await screen.findByText('Sources')
    const links = screen.getAllByRole('link', { name: /^S\d / })
    expect(rowText(links.find((l) => l.textContent?.includes('Turbopump overview')))).toContain(
      'Classification: OFFICIAL',
    )
    expect(rowText(links.find((l) => l.textContent?.includes('Igniter spec')))).toContain('Classification: SECRET')
  })

  it('badges show the level only — a citation row never claims to be the whole marking', async () => {
    renderAsk(() => answered('The impeller [S1], the igniter [S2].'))
    askQuestion('q')
    await screen.findByText('Sources')
    const sources = screen.getAllByRole('link', { name: /^S\d / })
    // The source page's caveat is enforced server-side and shown on the page
    // itself; a row has no space for it. The only EYES ONLY text on screen is
    // the aggregate's, which is a banner, not a row.
    for (const row of sources) expect(row.textContent).not.toContain('EYES ONLY')
  })

  it('a null aggregate renders no banner at all — not "UNMARKED", not a placeholder', async () => {
    // §21.13: no sources means no label. OFFICIAL would assert a judgement
    // about content that does not exist; TOP SECRET would invent a fact.
    renderAsk(() => answered('Answered from nothing.', [], null))
    askQuestion('q')
    await screen.findByText('Answered from nothing.')
    expect(document.querySelector('[data-aggregate-marking]')).toBeNull()
    expect(screen.queryByText(/UNMARKED|Unmarked|Not marked/)).toBeNull()
    expect(screen.queryByText(/Protective marking/)).toBeNull()
    // …and the announcement drops it too rather than saying nothing is marked.
    expect(screen.getByRole('status')).toHaveTextContent('Answer ready.')
    expect(screen.getByRole('status').textContent).not.toContain('Protective marking')
  })

  it('says what the aggregate covers, because it can out-rank every citation on screen', async () => {
    // The aggregate spans everything that entered the model context, cited or
    // not — without saying so, a SECRET banner over two OFFICIAL citations
    // reads as a bug.
    renderAsk(() => answered('Only the open source is cited [S1].', [CITATIONS[0]]))
    askQuestion('q')
    await screen.findByText(/Only the open source/)
    expect(screen.getByText('Covers every page this answer drew on, including any not cited.')).toBeInTheDocument()
  })

  it('has no axe violations with a marked answer, marked citations and both banners', async () => {
    renderAsk(() => answered('The impeller [S1], the igniter [S2].', CITATIONS, CONJUNCTIVE))
    askQuestion('q')
    await screen.findByText('Sources')
    await expectNoAxeViolations()
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
    // the probe — the status query reports shape, not availability, and
    // pauses once the store has collapsed).
    const mock = createMockUrqlClient((name) =>
      name === 'AssistantStatus'
        ? { assistantStatus: { configured: false, maxQuestionChars: null } }
        : unavailable('NOT_CONFIGURED'),
    )
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

/**
 * A question the server refused for length.
 *
 * The refusal is the design: an over-long question is rejected outright rather
 * than truncated, so the person who wrote it decides what to cut instead of
 * silently receiving an answer to some prefix of what they asked. Softening
 * that into a generic failure would throw away the only reason the server
 * behaves this way.
 */
describe('AskWikiPage — QUESTION_TOO_LONG', () => {
  // Trimmed, because the composer trims before sending — the length the page
  // reports is the length of what actually went.
  const LONG = 'why does the turbopump cavitate '.repeat(80).trim()

  it('says it was refused, not shortened, and that nothing was answered', async () => {
    renderAsk(() => unavailable('QUESTION_TOO_LONG'))
    askQuestion(LONG)

    const copy = await screen.findByText(/too long for this wiki/)
    expect(copy).toHaveTextContent(/refused rather than shortened for you/)
    expect(copy).toHaveTextContent(/nothing was answered/)
  })

  it('states the length of the question the user actually sent', async () => {
    // The instance's limit is server configuration and is not on the wire, so
    // the SPA cannot quote it without guessing. What it can state truthfully is
    // the size of what was just typed — which is also what tells you how much
    // to cut.
    renderAsk(() => unavailable('QUESTION_TOO_LONG'))
    askQuestion(LONG)

    expect(await screen.findByText(new RegExp(`Yours was ${LONG.length.toLocaleString()} characters`))).toBeInTheDocument()
  })

  it('names THIS instance’s limit, not the server default', async () => {
    // The number has to come from `assistantStatus.maxQuestionChars`. A figure
    // baked into the SPA would be right only on a default install and would
    // tell someone on a lower-limit instance to cut to a length that would be
    // refused all over again.
    renderAsk(() => unavailable('QUESTION_TOO_LONG'), { maxQuestionChars: 350 })
    askQuestion(LONG)

    const copy = await screen.findByText(/too long for this wiki/)
    expect(copy).toHaveTextContent('This wiki accepts up to 350 characters.')
    expect(copy.textContent).not.toMatch(/2,?000/)
  })

  it('names no limit at all when the instance did not report one', async () => {
    // Unconfigured assistant, anonymous caller, or a status query that failed —
    // all arrive as null, and the copy falls back to exactly what it said
    // before the field existed rather than to a guess.
    renderAsk(() => unavailable('QUESTION_TOO_LONG'), { maxQuestionChars: null })
    askQuestion(LONG)

    const copy = await screen.findByText(/too long for this wiki/)
    expect(copy).toHaveTextContent(/refused rather than shortened for you/)
    expect(copy.textContent).not.toMatch(/accepts up to/)
    expect(copy.textContent).not.toMatch(/\d/)
  })

  it('leaves the composer in place so the question can be shortened and re-asked', async () => {
    renderAsk(() => unavailable('QUESTION_TOO_LONG'))
    askQuestion(LONG)
    await screen.findByText(/too long for this wiki/)

    expect(screen.getByLabelText('Ask a question')).toBeInTheDocument()
  })

  it('offers a search escape hatch that is not itself over-long', async () => {
    renderAsk(() => unavailable('QUESTION_TOO_LONG'))
    askQuestion(LONG)

    const link = await screen.findByRole('link', { name: 'search for the words' })
    const href = link.getAttribute('href') ?? ''
    expect(href.startsWith('/search?q=')).toBe(true)
    expect(href.length).toBeLessThan(LONG.length)
  })

  it('has no axe violations in the refused state', async () => {
    renderAsk(() => unavailable('QUESTION_TOO_LONG'))
    askQuestion(LONG)
    await screen.findByText(/too long for this wiki/)
    await expectNoAxeViolations()
  })
})

/**
 * The soft counter beside the composer.
 *
 * It exists so the refusal is not the first anyone hears about the limit — and
 * it is soft on purpose: the number was fetched at mount and the server remains
 * the authority, so this warns and never blocks. A disabled Ask button would be
 * the client refusing on a figure that may have moved, with no way to explain
 * itself; the designed refusal is a better outcome than that.
 *
 * Every length below is derived from the limit the mocked status query reports,
 * never from a literal, so the counter cannot quietly agree with 2,000 alone.
 */
describe('AskWikiPage — question length counter', () => {
  const type = (text: string) => fireEvent.change(screen.getByLabelText('Ask a question'), { target: { value: text } })
  const counter = () => document.querySelector('[aria-live="polite"]')?.textContent ?? ''

  it('says nothing while the question is well inside the limit', () => {
    renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type('x'.repeat(50))
    expect(counter()).toBe('')
  })

  it('appears as the question approaches the limit', () => {
    renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type('x'.repeat(370))
    expect(counter()).toContain('30 characters left of 400')
  })

  it('at exactly the limit it warns but does not claim the question is over', async () => {
    // The server accepts a question of exactly MaxQuestionChars, so the counter
    // must not say it will be refused.
    renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type('x'.repeat(400))
    expect(counter()).toContain('0 characters left')
    expect(counter()).not.toContain('over')
    expect(screen.getByLabelText('Ask a question')).toBeValid()
  })

  it('one character later it says the question will be refused, not shortened', () => {
    renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type('x'.repeat(401))
    expect(counter()).toContain('1 character over the 400 this wiki accepts')
    expect(counter()).toContain('refused, not shortened')
  })

  it('warns but never blocks — Ask stays pressable and the question still goes', async () => {
    const { mock } = renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type('x'.repeat(500))

    const ask = screen.getByRole('button', { name: 'Ask' })
    expect(ask).toBeEnabled()
    fireEvent.click(ask)

    await waitFor(() => expect(mock.operations.filter((o) => o.name === 'AskWiki')).toHaveLength(1))
  })

  it('counts what will be SENT, not what is typed — the composer trims first', () => {
    // Trailing whitespace the server never sees must not push the counter over.
    renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type(`${'x'.repeat(400)}${' '.repeat(50)}`)
    expect(counter()).not.toContain('over')
  })

  it('says nothing when the instance reported no limit', () => {
    renderAsk(() => answered('a'), { maxQuestionChars: null })
    type('x'.repeat(100000))
    expect(counter()).toBe('')
  })

  it('has no axe violations with the over-limit warning showing', async () => {
    renderAsk(() => answered('a'), { maxQuestionChars: 400 })
    type('x'.repeat(500))
    await expectNoAxeViolations()
  })
})
