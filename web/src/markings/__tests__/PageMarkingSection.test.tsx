import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { Client, Provider as UrqlProvider, type Exchange, type Operation, CombinedError } from 'urql'
import { Kind, type OperationDefinitionNode } from 'graphql'
import { filter, map, pipe } from 'wonka'
import { PageMarkingSection } from '../PageMarkingSection'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The marking control (design.md §21). Three things it has to get right
 * beyond rendering: it must PREVENT the two markings the server refuses — a
 * selector value the caller is not granted here (§21.15) and a caveat that
 * excludes them (§21.4) — rather than offer them and report the refusal; it
 * must never assemble a marking string — `label` is the server's, and there
 * is exactly one formatter; and every vocabulary it offers is the server's —
 * level spellings AND order from the scheme, the five caveat countries
 * pinned to the schema, selector categories from configuration.
 *
 * And one thing it must NOT do: grey a level. This deployment carries no
 * clearance, so the classification is display, compared against nobody, and
 * every level is offered to every editor. The "every level is offered" test
 * below is the one that goes red if a level check ever creeps back in.
 */

interface Selector {
  category: string
  value: string
}

const baseMarking = {
  level: 'OFFICIAL',
  levelName: 'OFFICIAL',
  eyesOnly: [] as string[],
  selectors: [] as Selector[],
  ukPrefix: true,
  label: 'UK OFFICIAL',
}

/** §21.1: the levels with their UK written spellings, in scheme order. */
const scheme = [
  { level: 'OFFICIAL', name: 'OFFICIAL' },
  { level: 'OFFICIAL_SENSITIVE', name: 'OFFICIAL-SENSITIVE' },
  { level: 'SECRET', name: 'SECRET' },
  { level: 'TOP_SECRET', name: 'TOP SECRET' },
]

const EVERY_LEVEL = ['OFFICIAL', 'OFFICIAL_SENSITIVE', 'SECRET', 'TOP_SECRET']

/** §21.15: the instance's configured categories — the test catalog. */
const catalog = [
  { name: 'FRUIT', description: 'Fruit programme compartments', values: ['APPLE', 'BANANA'] },
  { name: 'REGION', description: null, values: ['NORTH', 'SOUTH'] },
]

interface Options {
  marking?: typeof baseMarking
  canEdit?: boolean
  nationality?: string[]
  /** null stages an unanswered grants read, so the picker's "unknown grants" path is exercised. */
  grants?: Selector[] | null
  /** null stages an unanswered categories read. */
  categories?: typeof catalog | null
  /** null stages an unanswered classificationScheme read. */
  levelNames?: typeof scheme | null
  /** The scheme read fails at the transport — the absence of an answer, not an empty one. */
  schemeFails?: boolean
  setMarkingError?: Record<string, unknown> | null
}

function operationName(op: Operation): string {
  const def = op.query.definitions.find((d): d is OperationDefinitionNode => d.kind === Kind.OPERATION_DEFINITION)
  return def?.name?.value ?? '(anonymous)'
}

/** Like the mock client, except that ONE named operation fails at the transport. */
function clientFailingOnly(failing: string, respond: (name: string) => Record<string, unknown> | undefined): Client {
  const exchange: Exchange = () => (ops$) =>
    pipe(
      ops$,
      filter((op: Operation) => op.kind !== 'teardown'),
      map((op: Operation) => {
        const name = operationName(op)
        return name === failing
          ? {
              operation: op,
              data: undefined,
              error: new CombinedError({ networkError: new Error('offline') }),
              stale: false,
              hasNext: false,
            }
          : { operation: op, data: respond(name), stale: false, hasNext: false }
      }),
    )
  return new Client({ url: '/graphql', exchanges: [exchange] })
}

function renderSection(options: Options = {}) {
  const {
    marking = baseMarking,
    canEdit = true,
    nationality = ['UK'],
    grants = [
      { category: 'FRUIT', value: 'APPLE' },
      { category: 'REGION', value: 'NORTH' },
      { category: 'REGION', value: 'SOUTH' },
    ],
    categories = catalog,
    levelNames = scheme,
    schemeFails = false,
    setMarkingError = null,
  } = options
  const onFeedback = vi.fn()
  const onReplicaRefusal = vi.fn()
  const onChanged = vi.fn()
  const respond = (name: string): Record<string, unknown> | undefined => {
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1', email: null, name: 'Editor', groups: [], isAuthenticated: true,
          isInstanceAdmin: false, localUserId: 'user-1', hasAvatar: false,
          nationality,
        },
      }
    if (name === 'ClassificationScheme') return levelNames === null ? undefined : { classificationScheme: levelNames }
    if (name === 'SelectorCategories') return categories === null ? undefined : { selectorCategories: categories }
    if (name === 'SpaceSelectorGrants')
      return grants === null ? undefined : { space: { id: 'space-1', key: 'ENG', viewerSelectorGrants: grants } }
    if (name === 'SetPageMarking')
      return {
        setPageMarking: {
          marking: setMarkingError ? null : { ...marking, level: 'SECRET', label: 'UK SECRET' },
          error: setMarkingError,
        },
      }
    return undefined
  }
  const mock = createMockUrqlClient((name) => respond(name))
  render(
    <UrqlProvider value={schemeFails ? clientFailingOnly('ClassificationScheme', respond) : mock.client}>
      <PageMarkingSection
        pageId="page-1"
        spaceKey="ENG"
        marking={marking as never}
        canEdit={canEdit}
        onFeedback={onFeedback}
        onReplicaRefusal={onReplicaRefusal}
        onChanged={onChanged}
      />
    </UrqlProvider>,
  )
  return { mock, onFeedback, onReplicaRefusal, onChanged }
}

/**
 * Keyed by the ENUM value, not the visible label: the spelling on screen is
 * the server's (§21.1), so an assertion about which level a radio IS must
 * not go through the text — that would couple every one of these tests to a
 * display decision the SPA does not own.
 */
const levelRadio = (level: string) => {
  const radio = document.querySelector<HTMLInputElement>(`input[type="radio"][value="${level}"]`)
  if (!radio) throw new Error(`no radio rendered for level ${level}`)
  return radio
}

const renderedLevels = () => [...document.querySelectorAll<HTMLInputElement>('input[type="radio"]')].map((r) => r.value)

/** The level radios come from the scheme read, so they arrive a tick after the form does. */
const levelsRendered = () => waitFor(() => expect(renderedLevels().length).toBeGreaterThan(0))

const saveButton = () => screen.findByRole('button', { name: 'Save marking' })

interface SentInput {
  pageId: string
  level: string
  eyesOnly: string[]
  selectors: Selector[]
  ukPrefix: boolean
}

function sentMarking(mock: ReturnType<typeof renderSection>['mock']): SentInput {
  const op = mock.operations.find((o) => o.name === 'SetPageMarking')
  if (!op) throw new Error('SetPageMarking was not sent')
  return (op.variables as { input: SentInput }).input
}

async function openSelect(label: string) {
  fireEvent.mouseDown(await screen.findByLabelText(label))
  return screen.findByRole('listbox')
}

describe('rendering the marking', () => {
  it("renders the server's label verbatim rather than composing prefix + level + selectors + caveat", () => {
    renderSection({
      marking: {
        level: 'SECRET',
        levelName: 'SECRET',
        eyesOnly: ['UK', 'US'],
        selectors: [{ category: 'FRUIT', value: 'APPLE' }],
        ukPrefix: true,
        label: 'UK SECRET APPLE UK/US EYES ONLY',
      },
    })
    expect(screen.getByText('UK SECRET APPLE UK/US EYES ONLY')).toBeTruthy()
  })

  it('renders a bare level for a marking with no prefix — §21.12 keeps the toggle off legal and unpadded', () => {
    renderSection({
      marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: [], selectors: [], ukPrefix: false, label: 'TOP SECRET' },
    })
    // Scoped to the read-out: the picker offers an option spelled the same
    // way (§21.1's display name), and this assertion is about the MARKING.
    expect(document.querySelector('[data-marking-placement="section"]')?.textContent).toBe('TOP SECRET')
  })

  it('shows a viewer the marking with no controls at all (design.md §21.6 needs canEdit)', () => {
    renderSection({ canEdit: false })
    expect(screen.getByText('UK OFFICIAL')).toBeTruthy()
    expect(screen.queryByRole('radiogroup')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Save marking' })).toBeNull()
  })

  it('has no axe violations in the editable state', async () => {
    renderSection()
    await saveButton()
    await levelsRendered()
    await screen.findByLabelText('FRUIT')
    await expectNoAxeViolations()
  })
})

describe('the level picker names and orders levels from the server (design.md §21.1)', () => {
  it('labels each option with the scheme display spelling, not the wire name', async () => {
    renderSection()
    expect(await screen.findByText('OFFICIAL-SENSITIVE')).toBeTruthy()
    expect(screen.getByText('TOP SECRET')).toBeTruthy()
    expect(screen.queryByText('OFFICIAL_SENSITIVE')).toBeNull()
    expect(screen.queryByText('TOP_SECRET')).toBeNull()
  })

  it('renders options in the order the scheme returns them, and never re-sorts', async () => {
    // The scheme arrives least-sensitive first; the SPA encodes no order of
    // its own, so a scheme staged backwards renders backwards.
    renderSection({ levelNames: [...scheme].reverse() })
    await levelsRendered()
    expect(renderedLevels()).toEqual(['TOP_SECRET', 'SECRET', 'OFFICIAL_SENSITIVE', 'OFFICIAL'])
  })

  it("keeps offering the page's own level, under the server's spelling, when the scheme does not name it", async () => {
    // A server ahead of this client: what the page carries can still be seen
    // and kept, and the spelling is the server's `levelName`, never derived.
    renderSection({
      marking: { ...baseMarking, level: 'COSMIC', levelName: 'COSMIC TOP SECRET', label: 'UK COSMIC TOP SECRET' },
    })
    await levelsRendered()
    expect(renderedLevels()).toEqual([...EVERY_LEVEL, 'COSMIC'])
    expect(levelRadio('COSMIC').checked).toBe(true)
    expect(screen.getByRole('radio', { name: 'COSMIC TOP SECRET' })).toBeTruthy()
  })

  it('offers no level while the scheme has not answered, rather than spelling any itself', async () => {
    renderSection({ levelNames: null })
    await saveButton()
    expect(renderedLevels()).toEqual([])
    expect(screen.queryByText('OFFICIAL_SENSITIVE')).toBeNull()
  })

  it('says so when the scheme cannot be loaded, and still lets the rest of the marking be saved', async () => {
    const { onFeedback } = renderSection({ schemeFails: true })
    const save = await saveButton()
    expect(await screen.findByText(/Couldn't load the classification levels/)).toBeTruthy()
    expect(renderedLevels()).toEqual([])
    // The prefix, the caveat and the selectors do not depend on the scheme.
    fireEvent.click(screen.getByLabelText('UK prefix'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(onFeedback).toHaveBeenCalledWith({ notice: 'Marking set to UK SECRET.', error: null }))
  })
})

describe('every level is offered to everyone — the classification is display', () => {
  it('offers all four levels enabled, whatever the caller holds', async () => {
    // The weakest caller there is: no nationality and no grants at all. The
    // two gates that remain would refuse plenty, and none of it is about
    // the level — nothing about a person can make a level unavailable.
    renderSection({ nationality: [], grants: [] })
    await levelsRendered()
    expect(renderedLevels()).toEqual(EVERY_LEVEL)
    for (const level of EVERY_LEVEL) {
      expect(levelRadio(level).disabled).toBe(false)
      expect(screen.getByRole('radio', { name: scheme.find((s) => s.level === level)!.name })).not.toBeDisabled()
    }
    // No reason travels with any option, because there is none to give.
    expect(document.body.textContent).not.toMatch(/clearance|eligib|above your/i)
  })

  it('says beside the picker that the level is display, like the prefix', async () => {
    renderSection()
    expect(
      await screen.findByText('Any level may be set. Like the prefix, it is shown on the page and compared against nobody.'),
    ).toBeTruthy()
  })

  it('lets TOP SECRET be chosen and saved with no refusal, by a caller holding nothing', async () => {
    const { mock } = renderSection({ nationality: [], grants: [] })
    const save = await saveButton()
    await levelsRendered()
    fireEvent.click(levelRadio('TOP_SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    expect(screen.queryByRole('alert')).toBeNull()
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    expect(sentMarking(mock).level).toBe('TOP_SECRET')
  })
})

describe('the eyes-only caveat draws on the fixed five (design.md §21.4/§21.6)', () => {
  it('offers exactly AUS, CAN, NZ, UK and US — no registry, no ISO list, no GB', async () => {
    renderSection()
    const field = await screen.findByLabelText('Eyes only')
    fireEvent.mouseDown(field)
    fireEvent.focus(field)
    fireEvent.change(field, { target: { value: '' } })
    const listbox = await screen.findByRole('listbox')
    expect(within(listbox).getAllByRole('option').map((o) => o.textContent)).toEqual(['AUS', 'CAN', 'NZ', 'UK', 'US'])
  })

  it('blocks the save and explains when the set would exclude the caller', async () => {
    renderSection({ nationality: ['UK'], marking: { ...baseMarking, eyesOnly: ['UK'] } })
    const save = await saveButton()
    const field = screen.getByLabelText('Eyes only')
    // Drop UK, add US: a set the caller holds nothing in.
    fireEvent.keyDown(field, { key: 'Backspace' })
    fireEvent.mouseDown(field)
    fireEvent.change(field, { target: { value: 'US' } })
    fireEvent.click(await screen.findByRole('option', { name: 'US' }))
    await waitFor(() => expect(screen.getByText(/holds none of your own nationalities/)).toBeTruthy())
    expect((save as HTMLButtonElement).disabled).toBe(true)
  })

  it('says so up front when the caller holds no nationality at all', async () => {
    renderSection({ nationality: [] })
    const field = await screen.findByLabelText('Eyes only')
    fireEvent.mouseDown(field)
    fireEvent.change(field, { target: { value: 'NZ' } })
    fireEvent.click(await screen.findByRole('option', { name: 'NZ' }))
    await waitFor(() => expect(screen.getByText(/You hold no nationality value/)).toBeTruthy())
  })

  it('warns that a legacy caveat token the mutation cannot send will be dropped on save', async () => {
    // A token stored before the vocabulary was fixed is readable (output is a
    // string list) but not writable (input is the enum): said up front.
    renderSection({ marking: { ...baseMarking, eyesOnly: ['GB'], label: 'UK OFFICIAL GB EYES ONLY' } })
    await saveButton()
    expect(screen.getByText(/This marking names GB/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'GB' })).toBeNull()
  })
})

describe('prevent, do not refuse — selectors (design.md §21.15)', () => {
  it('offers one picker per configured category, from the server, none typed by hand', async () => {
    renderSection()
    expect(await screen.findByLabelText('FRUIT')).toBeTruthy()
    expect(screen.getByLabelText('REGION')).toBeTruthy()
    expect(screen.getByText('Fruit programme compartments')).toBeTruthy()
  })

  it('renders no selector pickers on an instance that configures no categories', async () => {
    renderSection({ categories: [] })
    await saveButton()
    expect(screen.queryByLabelText('FRUIT')).toBeNull()
    expect(screen.queryByText('Selectors')).toBeNull()
  })

  it('never disables a whole category — a category has no eligibility to lack, only values to be granted', async () => {
    // A caller granted nothing in this space: every picker is still live,
    // and it is the VALUES inside that are greyed, each with its reason.
    renderSection({ grants: [] })
    expect(await screen.findByLabelText('FRUIT')).not.toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByLabelText('REGION')).not.toHaveAttribute('aria-disabled', 'true')
    expect(document.body.textContent).not.toMatch(/eligib/i)
    const listbox = await openSelect('FRUIT')
    expect(within(listbox).getByRole('option', { name: /APPLE/ })).toHaveAttribute('aria-disabled', 'true')
    expect(within(listbox).getByRole('option', { name: /BANANA/ })).toHaveAttribute('aria-disabled', 'true')
    expect(within(listbox).getByRole('option', { name: 'None' })).not.toHaveAttribute('aria-disabled', 'true')
  })

  it('greys only a value no access grant confers on the caller in this space, with the reason', async () => {
    renderSection({ grants: [{ category: 'FRUIT', value: 'APPLE' }] })
    const listbox = await openSelect('FRUIT')
    expect(within(listbox).getByRole('option', { name: 'APPLE' })).not.toHaveAttribute('aria-disabled', 'true')
    const banana = within(listbox).getByRole('option', { name: /BANANA/ })
    expect(banana).toHaveAttribute('aria-disabled', 'true')
    expect(banana).toHaveTextContent('Not granted to you in this space')
  })

  it('greys nothing while the grants are unknown — the server still decides', async () => {
    renderSection({ grants: null })
    const listbox = await openSelect('FRUIT')
    expect(within(listbox).getByRole('option', { name: 'BANANA' })).not.toHaveAttribute('aria-disabled', 'true')
  })

  it("keeps offering the page's own value when the catalog no longer lists it, so it can be seen and cleared", async () => {
    renderSection({ marking: { ...baseMarking, selectors: [{ category: 'FRUIT', value: 'CHERRY' }], label: 'UK OFFICIAL CHERRY' } })
    expect(await screen.findByLabelText('FRUIT')).toHaveTextContent('CHERRY')
  })

  it('says why when the page already carries a value the caller is not granted here', async () => {
    renderSection({
      grants: [{ category: 'FRUIT', value: 'APPLE' }],
      marking: { ...baseMarking, selectors: [{ category: 'FRUIT', value: 'BANANA' }], label: 'UK OFFICIAL BANANA' },
    })
    await waitFor(() =>
      expect(
        screen.getByText('BANANA is not granted to you in this space, so you could not read this page after marking it.'),
      ).toBeTruthy(),
    )
  })

  it('states the one rule a value has to satisfy', async () => {
    renderSection()
    expect(
      await screen.findByText(
        'At most one value per category. A reader must hold an access grant in this space that carries each value.',
      ),
    ).toBeTruthy()
  })

  it('sends the chosen selectors as category/value pairs, one per category', async () => {
    const { mock } = renderSection()
    const save = await saveButton()
    const listbox = await openSelect('FRUIT')
    fireEvent.click(within(listbox).getByRole('option', { name: 'APPLE' }))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    expect(sentMarking(mock).selectors).toEqual([{ category: 'FRUIT', value: 'APPLE' }])
  })

  it('clears a selector by choosing None', async () => {
    const { mock } = renderSection({
      marking: { ...baseMarking, selectors: [{ category: 'FRUIT', value: 'APPLE' }], label: 'UK OFFICIAL APPLE' },
    })
    const save = await saveButton()
    const listbox = await openSelect('FRUIT')
    fireEvent.click(within(listbox).getByRole('option', { name: 'None' }))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    expect(sentMarking(mock).selectors).toEqual([])
  })
})

describe('the UK prefix is a switch (design.md §21.12)', () => {
  it('says it is presentational, and sends ukPrefix: false when turned off', async () => {
    const { mock } = renderSection()
    const save = await saveButton()
    expect(screen.getByText('Presentational only. It grants and denies nothing.')).toBeTruthy()
    const toggle = screen.getByLabelText('UK prefix') as HTMLInputElement
    expect(toggle.checked).toBe(true)
    fireEvent.click(toggle)
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    expect(sentMarking(mock).ukPrefix).toBe(false)
  })
})

describe('saving', () => {
  it('sends the whole marking — level, enum caveat, selectors and the prefix flag, every time', async () => {
    const { mock } = renderSection({
      marking: {
        ...baseMarking,
        eyesOnly: ['us', 'UK'],
        selectors: [{ category: 'REGION', value: 'NORTH' }],
        label: 'UK OFFICIAL NORTH UK/US EYES ONLY',
      },
    })
    const save = await saveButton()
    await levelsRendered()
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    expect(sentMarking(mock)).toEqual({
      pageId: 'page-1',
      level: 'SECRET',
      eyesOnly: ['UK', 'US'],
      selectors: [{ category: 'REGION', value: 'NORTH' }],
      ukPrefix: true,
    })
  })

  it('offers no save until something actually changed', async () => {
    renderSection()
    const save = await saveButton()
    expect((save as HTMLButtonElement).disabled).toBe(true)
  })

  it('reports the new marking using the label the server sent back', async () => {
    const { onFeedback } = renderSection()
    const save = await saveButton()
    await levelsRendered()
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(onFeedback).toHaveBeenCalledWith({ notice: 'Marking set to UK SECRET.', error: null }))
  })

  it('surfaces a server refusal instead of swallowing it — a grant can change mid-session', async () => {
    const { onFeedback } = renderSection({
      setMarkingError: { kind: 'Forbidden', message: 'selector:not_granted:FRUIT' },
    })
    const save = await saveButton()
    await levelsRendered()
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() =>
      expect(onFeedback).toHaveBeenCalledWith({
        notice: null,
        error: 'Not permitted: selector:not_granted:FRUIT',
      }),
    )
  })

  it('routes a replica refusal to the replica explainer, never an error line (design.md §12)', async () => {
    const { onReplicaRefusal } = renderSection({
      setMarkingError: { kind: 'ReadOnlyReplica', spaceId: 'space-1', originInstanceId: 'LOW' },
    })
    const save = await saveButton()
    await levelsRendered()
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(onReplicaRefusal).toHaveBeenCalledWith('LOW'))
  })
})

describe('the section is not a property row (design.md §20 vs §21)', () => {
  it('has its own heading and says which parts of a marking decide who may read the page', () => {
    renderSection()
    const heading = screen.getByRole('heading', { name: 'Protective marking' })
    const section = heading.closest('div')
    expect(section).toBeTruthy()
    expect(within(section as HTMLElement).getByText(/decide who may read this page/)).toBeTruthy()
    expect(
      within(section as HTMLElement).getByText(/the classification and the prefix are presentational and decide nothing/),
    ).toBeTruthy()
  })
})
