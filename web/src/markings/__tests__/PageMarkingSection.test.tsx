import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { PageMarkingSection } from '../PageMarkingSection'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The marking control (design.md §21). Two things it has to get right beyond
 * rendering: it must PREVENT the two markings §21.6 refuses rather than offer
 * them and report the refusal, and it must never assemble a marking string —
 * `label` is the server's, and there is exactly one formatter.
 */

const baseMarking = {
  level: 'OFFICIAL',
  levelName: 'OFFICIAL',
  eyesOnly: [] as string[],
  prefix: 'UK',
  label: 'UK OFFICIAL',
}

/** §21.1: the levels with their UK written spellings, in scheme order. */
const scheme = [
  { level: 'OFFICIAL', name: 'OFFICIAL' },
  { level: 'OFFICIAL_SENSITIVE', name: 'OFFICIAL-SENSITIVE' },
  { level: 'SECRET', name: 'SECRET' },
  { level: 'TOP_SECRET', name: 'TOP SECRET' },
]

interface Options {
  marking?: typeof baseMarking
  canEdit?: boolean
  clearance?: string
  nationality?: string[]
  /** null stages a registry with no `nationality` attribute at all (§21.4's accepted consequence). */
  countries?: string[] | null
  /** null stages an unanswered classificationScheme, so the picker's fallback is exercised. */
  levelNames?: typeof scheme | null
  setMarkingError?: Record<string, unknown> | null
}

function renderSection(options: Options = {}) {
  const {
    marking = baseMarking,
    canEdit = true,
    clearance = 'SECRET',
    nationality = ['UK'],
    countries = ['UK', 'US', 'AU'],
    levelNames = scheme,
    setMarkingError = null,
  } = options
  const onFeedback = vi.fn()
  const onReplicaRefusal = vi.fn()
  const onChanged = vi.fn()
  const mock = createMockUrqlClient((name) => {
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1', email: null, name: 'Editor', groups: [], isAuthenticated: true,
          isInstanceAdmin: false, localUserId: 'user-1', hasAvatar: false, clearance, nationality,
        },
      }
    if (name === 'ClassificationScheme') return levelNames === null ? undefined : { classificationScheme: levelNames }
    if (name === 'RuleVocabulary')
      return {
        groups: [],
        attributeRegistry:
          countries === null ? [] : [{ key: 'nationality', displayName: 'Nationality', allowedValues: countries }],
      }
    if (name === 'SetPageMarking')
      return {
        setPageMarking: {
          marking: setMarkingError ? null : { ...marking, level: 'SECRET', label: 'UK SECRET' },
          error: setMarkingError,
        },
      }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <PageMarkingSection
        pageId="page-1"
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
 * the server's now (§21.1), so an assertion about which level a radio IS must
 * not go through the text — that would couple every one of these tests to a
 * display decision the SPA does not own.
 */
const levelRadio = (level: string) => {
  const radio = document.querySelector<HTMLInputElement>(`input[type="radio"][value="${level}"]`)
  if (!radio) throw new Error(`no radio rendered for level ${level}`)
  return radio
}

describe('rendering the marking', () => {
  it("renders the server's label verbatim rather than composing prefix + level + caveat", () => {
    renderSection({
      marking: {
        level: 'SECRET',
        levelName: 'SECRET',
        eyesOnly: ['UK', 'US'],
        prefix: 'UK',
        label: 'UK SECRET [UK/US EYES ONLY]',
      },
    })
    expect(screen.getByText('UK SECRET [UK/US EYES ONLY]')).toBeTruthy()
  })

  it('renders a bare level for a marking with no prefix — §21.12 keeps null legal and unpadded', () => {
    renderSection({
      marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: [], prefix: null as never, label: 'TOP SECRET' },
    })
    // Scoped to the read-out: the picker now offers an option spelled the same
    // way (§21.1's display name), and this assertion is about the MARKING, so
    // it must not be able to pass by finding the option instead.
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
    await screen.findByRole('button', { name: 'Save marking' })
    await expectNoAxeViolations()
  })
})

describe('the level picker names levels from the server (design.md §21.1)', () => {
  it('labels each option with the scheme display spelling, not the wire name', async () => {
    renderSection()
    expect(await screen.findByText('OFFICIAL-SENSITIVE')).toBeTruthy()
    expect(screen.getByText('TOP SECRET')).toBeTruthy()
    // A picker has no marking in hand, so these cannot come from `levelName`
    // — and spelling them locally is the drift §21.1 exists to prevent.
    expect(screen.queryByText('OFFICIAL_SENSITIVE')).toBeNull()
    expect(screen.queryByText('TOP_SECRET')).toBeNull()
  })

  it('still offers every level when the scheme query answers nothing', async () => {
    // The ladder is compile-time exhaustive, so an unanswered vocabulary query
    // degrades to ugly wire names rather than a picker with holes in it.
    renderSection({ levelNames: null })
    await screen.findByRole('button', { name: 'Save marking' })
    for (const level of ['OFFICIAL', 'OFFICIAL_SENSITIVE', 'SECRET', 'TOP_SECRET']) {
      expect(levelRadio(level)).toBeTruthy()
    }
  })

  it('renders options in ladder order, so position never contradicts availability', async () => {
    renderSection({ clearance: 'OFFICIAL_SENSITIVE' })
    await screen.findByRole('button', { name: 'Save marking' })
    const rendered = [...document.querySelectorAll<HTMLInputElement>('input[type="radio"]')].map((r) => r.value)
    expect(rendered).toEqual(['OFFICIAL', 'OFFICIAL_SENSITIVE', 'SECRET', 'TOP_SECRET'])
    // Everything below the cut is available and everything above it is not,
    // which only reads correctly because the order IS the comparison.
    expect(rendered.map((level) => levelRadio(level).disabled)).toEqual([false, false, true, true])
  })
})

describe('prevent, do not refuse — levels (design.md §21.6)', () => {
  it('offers every level at or below the caller clearance', async () => {
    renderSection({ clearance: 'SECRET' })
    await waitFor(() => expect(levelRadio('OFFICIAL_SENSITIVE').hasAttribute('disabled')).toBe(false))
    expect(levelRadio('OFFICIAL').hasAttribute('disabled')).toBe(false)
    expect(levelRadio('SECRET').hasAttribute('disabled')).toBe(false)
  })

  it('makes a level above the caller clearance unavailable, with the reason beside it', async () => {
    renderSection({ clearance: 'SECRET' })
    await waitFor(() => expect(levelRadio('TOP_SECRET').hasAttribute('disabled')).toBe(true))
    // The reason is text on the option, not a tooltip and not a colour — it
    // has to be readable before the choice, which is the whole point.
    expect(screen.getByText('Above your clearance')).toBeTruthy()
  })

  it('greys everything above OFFICIAL for a caller with no usable clearance (§21.3s floor)', async () => {
    renderSection({ clearance: 'OFFICIAL' })
    await waitFor(() => expect(levelRadio('OFFICIAL_SENSITIVE').hasAttribute('disabled')).toBe(true))
    expect(levelRadio('SECRET').hasAttribute('disabled')).toBe(true)
    expect(levelRadio('TOP_SECRET').hasAttribute('disabled')).toBe(true)
    expect(levelRadio('OFFICIAL').hasAttribute('disabled')).toBe(false)
  })
})

describe('prevent, do not refuse — the eyes-only caveat (design.md §21.4/§21.6)', () => {
  it('draws its options from the registered nationality attribute, not an ISO list', async () => {
    renderSection({ countries: ['GB', 'US'] })
    const field = await screen.findByLabelText('Eyes only')
    fireEvent.mouseDown(field)
    fireEvent.focus(field)
    fireEvent.change(field, { target: { value: '' } })
    await waitFor(() => expect(screen.getByRole('option', { name: 'GB' })).toBeTruthy())
    expect(screen.getByRole('option', { name: 'US' })).toBeTruthy()
    // An instance registering GB does not also get UK for free.
    expect(screen.queryByRole('option', { name: 'UK' })).toBeNull()
  })

  it('blocks the save and explains when the set would exclude the caller', async () => {
    renderSection({ nationality: ['UK'], marking: { ...baseMarking, eyesOnly: ['UK'] } })
    const save = await screen.findByRole('button', { name: 'Save marking' })
    const field = screen.getByLabelText('Eyes only')
    // Drop UK, add US: a set the caller holds nothing in.
    fireEvent.keyDown(field, { key: 'Backspace' })
    fireEvent.mouseDown(field)
    fireEvent.change(field, { target: { value: 'US' } })
    fireEvent.click(await screen.findByRole('option', { name: 'US' }))
    await waitFor(() =>
      expect(screen.getByText(/holds none of your own nationalities/)).toBeTruthy(),
    )
    expect((save as HTMLButtonElement).disabled).toBe(true)
  })

  it('says so up front when the caller holds no nationality at all', async () => {
    renderSection({ nationality: [] })
    const field = await screen.findByLabelText('Eyes only')
    fireEvent.mouseDown(field)
    fireEvent.change(field, { target: { value: 'UK' } })
    fireEvent.click(await screen.findByRole('option', { name: 'UK' }))
    await waitFor(() => expect(screen.getByText(/You hold no nationality value/)).toBeTruthy())
  })

  it('keeps the page\'s own countries selectable when the registry comes back empty', async () => {
    // §6.2 gates the attribute registry to rule managers; an editor who is not
    // one must still see — and be able to clear — what this page carries.
    renderSection({ countries: null, marking: { ...baseMarking, eyesOnly: ['UK'] } })
    const field = await screen.findByLabelText('Eyes only')
    expect((field as HTMLInputElement).disabled).toBe(false)
    expect(screen.getByText('UK')).toBeTruthy()
  })

  it('disables the picker and says why when there is nothing at all to name', async () => {
    renderSection({ countries: null })
    const field = await screen.findByLabelText('Eyes only')
    expect((field as HTMLInputElement).disabled).toBe(true)
    expect(screen.getByText(/No countries to pick from/)).toBeTruthy()
  })
})

describe('saving', () => {
  it('sends the whole marking — level, canonical country set and an explicit prefix', async () => {
    const { mock } = renderSection({ marking: { ...baseMarking, eyesOnly: ['us', 'UK'] } })
    const save = await screen.findByRole('button', { name: 'Save marking' })
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    const sent = mock.operations.find((op) => op.name === 'SetPageMarking')?.variables as {
      input: { level: string; eyesOnly: string[]; prefix: string | null }
    }
    expect(sent.input.level).toBe('SECRET')
    expect(sent.input.eyesOnly).toEqual(['UK', 'US'])
    expect(sent.input.prefix).toBe('UK')
  })

  it('sends prefix: null for a cleared prefix rather than omitting it into the UK default', async () => {
    const { mock } = renderSection()
    const save = await screen.findByRole('button', { name: 'Save marking' })
    fireEvent.change(screen.getByLabelText('National prefix'), { target: { value: '   ' } })
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(mock.operations.some((op) => op.name === 'SetPageMarking')).toBe(true))
    const sent = mock.operations.find((op) => op.name === 'SetPageMarking')?.variables as {
      input: { prefix: string | null }
    }
    expect(sent.input.prefix).toBeNull()
  })

  it('offers no save until something actually changed', async () => {
    renderSection()
    const save = await screen.findByRole('button', { name: 'Save marking' })
    expect((save as HTMLButtonElement).disabled).toBe(true)
  })

  it('reports the new marking using the label the server sent back', async () => {
    const { onFeedback } = renderSection()
    const save = await screen.findByRole('button', { name: 'Save marking' })
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(onFeedback).toHaveBeenCalledWith({ notice: 'Marking set to UK SECRET.', error: null }))
  })

  it('surfaces a server refusal instead of swallowing it — a clearance can change mid-session', async () => {
    const { onFeedback } = renderSection({
      setMarkingError: { kind: 'Forbidden', message: 'clearance below the resulting marking' },
    })
    const save = await screen.findByRole('button', { name: 'Save marking' })
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() =>
      expect(onFeedback).toHaveBeenCalledWith({
        notice: null,
        error: 'Not permitted: clearance below the resulting marking',
      }),
    )
  })

  it('routes a replica refusal to the replica explainer, never an error line (design.md §12)', async () => {
    const { onReplicaRefusal } = renderSection({
      setMarkingError: { kind: 'ReadOnlyReplica', spaceId: 'space-1', originInstanceId: 'LOW' },
    })
    const save = await screen.findByRole('button', { name: 'Save marking' })
    fireEvent.click(levelRadio('SECRET'))
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(save)
    await waitFor(() => expect(onReplicaRefusal).toHaveBeenCalledWith('LOW'))
  })
})

describe('the section is not a property row (design.md §20 vs §21)', () => {
  it('has its own heading and says what a marking is', () => {
    renderSection()
    const heading = screen.getByRole('heading', { name: 'Protective marking' })
    const section = heading.closest('div')
    expect(section).toBeTruthy()
    expect(within(section as HTMLElement).getByText(/decide who may read this page/)).toBeTruthy()
  })
})
