import { describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { PageDetailsPage } from '../PageDetailsPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The per-page properties screen (design.md §20). Two things it must get
 * right beyond rendering: permissions decide whether the editing affordances
 * exist at all (§20.2), and a cleared value becomes `removePageProperty` —
 * the server refuses an empty `setPageProperty` by design, and that refusal
 * describes no mistake the user made.
 */

const baseProperties = [
  { keyId: 'k-owner', key: 'Owner', value: 'Ada Lovelace', sortOrder: 0 },
  { keyId: 'k-status', key: 'Status', value: 'Draft', sortOrder: 1 },
]

const baseRegistry = [
  { id: 'k-owner', key: 'Owner', description: 'Who to ask about this page.', sortOrder: 0 },
  { id: 'k-status', key: 'Status', description: null, sortOrder: 1 },
  { id: 'k-review', key: 'Review Date', description: 'When this page is next reviewed.', sortOrder: 2 },
]

/** design.md §21.5: no page is unmarked, so the staged page always carries one. */
const baseMarking = {
  level: 'OFFICIAL',
  levelName: 'OFFICIAL',
  eyesOnly: [] as string[],
  ukPrefix: true, selectors: [],
  label: 'UK OFFICIAL',
}

interface Options {
  canEdit?: boolean
  canManageAccess?: boolean
  marking?: typeof baseMarking
  properties?: typeof baseProperties
  registry?: typeof baseRegistry
  pageMissing?: boolean
  isReplica?: boolean
  setError?: Record<string, unknown> | null
  removeError?: Record<string, unknown> | null
}

function renderPage(options: Options = {}) {
  const {
    canEdit = true,
    canManageAccess = false,
    marking = baseMarking,
    properties = baseProperties,
    registry = baseRegistry,
    pageMissing = false,
    isReplica = false,
    setError = null,
    removeError = null,
  } = options
  const mock = createMockUrqlClient((name) => {
    if (name === 'PagePropertiesForPage') {
      if (pageMissing) return { page: null }
      return { page: { id: 'page-1', title: 'Runbook', spaceKey: 'ENG', spaceId: 'space-1', canEdit, canManageAccess, marking, properties } }
    }
    if (name === 'PagePropertyKeys') return { pagePropertyKeys: registry }
    // The move dialog's destination tree lives on this screen now.
    if (name === 'SpaceTreeForMove') return { pageTree: [] }
    if (name === 'MovePage') return { movePage: { page: { id: 'page-1' }, error: null } }
    // The marking section (design.md §21) reads the caller's own clearance,
    // eligibility and per-space selector grants, and the instance's selector
    // categories; all are staged so this screen's property assertions aren't
    // testing the marking control by accident.
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-1', email: null, name: 'Editor', groups: [], isAuthenticated: true,
          isInstanceAdmin: false, localUserId: 'user-1', hasAvatar: false,
          clearance: 'SECRET', nationality: ['UK'], selectorEligibility: ['FRUIT'],
        },
      }
    if (name === 'SelectorCategories')
      return { selectorCategories: [{ name: 'FRUIT', description: null, requiresAttribute: true, values: ['APPLE', 'BANANA'] }] }
    if (name === 'SpaceSelectorGrants')
      return { space: { id: 'space-1', key: 'ENG', viewerSelectorGrants: [{ category: 'FRUIT', value: 'APPLE' }] } }
    if (name === 'ClassificationScheme')
      return {
        classificationScheme: [
          { level: 'OFFICIAL', name: 'OFFICIAL' },
          { level: 'OFFICIAL_SENSITIVE', name: 'OFFICIAL-SENSITIVE' },
          { level: 'SECRET', name: 'SECRET' },
          { level: 'TOP_SECRET', name: 'TOP SECRET' },
        ],
      }
    if (name === 'SpaceReplicaBanner')
      return { space: { id: 'space-1', key: 'ENG', isReplica, originInstanceId: isReplica ? 'LOW' : 'HIGH' } }
    if (name === 'SetPageProperty')
      return {
        setPageProperty: {
          property: setError ? null : { keyId: 'k-owner', key: 'Owner', value: 'Grace Hopper', sortOrder: 0 },
          error: setError,
        },
      }
    if (name === 'RemovePageProperty')
      return { removePageProperty: { removedPropertyKeyId: removeError ? null : 'k-status', error: removeError } }
    return undefined
  })
  render(
    <MemoryRouter initialEntries={['/pages/page-1/details']}>
      <UrqlProvider value={mock.client}>
        <Routes>
          <Route path="/pages/:pageId/details" element={<PageDetailsPage />} />
        </Routes>
      </UrqlProvider>
    </MemoryRouter>,
  )
  return mock
}

const mutations = (mock: ReturnType<typeof renderPage>, name: string) =>
  mock.operations.filter((op) => op.name === name).map((op) => op.variables)

describe('PageDetailsPage accessibility', () => {
  it('has no axe violations with the table, the add form, and a labelled field per row', async () => {
    renderPage()
    expect(await screen.findByRole('table', { name: 'Page properties' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Value for Owner' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Key' })).toBeInTheDocument()
    await expectNoAxeViolations()
  })

  it('has no axe violations on the read-only view a non-editor gets', async () => {
    renderPage({ canEdit: false })
    await screen.findByRole('table', { name: 'Page properties' })
    await expectNoAxeViolations()
  })
})

describe('PageDetailsPage permission-driven rendering (design.md §20.2)', () => {
  it('reads, but does not edit, for a viewer without canEdit', async () => {
    // REVERSES the editors-only gate this screen shipped with. That gate was
    // sound while the page view still rendered its own read-only properties
    // panel; it stopped being sound when this became the ONLY place properties
    // live, because it would then have taken them away from readers entirely.
    // Properties carry no restriction of their own (design.md 20.2).
    renderPage({ canEdit: false })
    const table = await screen.findByRole('table', { name: 'Page properties' })
    expect(table).toHaveTextContent('Owner')
    expect(table).toHaveTextContent('Ada Lovelace')
    // Read-only: no value fields, no remove buttons, no add form.
    expect(screen.queryByRole('textbox', { name: 'Value for Owner' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Remove Status' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Add a property' })).not.toBeInTheDocument()
  })

  it('offers Move only with canEdit', async () => {
    renderPage({ canEdit: false })
    await screen.findByRole('table', { name: 'Page properties' })
    expect(screen.queryByRole('button', { name: 'Move page' })).not.toBeInTheDocument()

    cleanup()
    renderPage({ canEdit: true })
    expect(await screen.findByRole('button', { name: 'Move page' })).toBeInTheDocument()
  })

  it('offers Permissions to a non-editor who can manage access', async () => {
    // The regression this move risked. canManageAccess is instance-admin OR
    // space-admin; canEdit is the rule-engine grant and is unconditionally false
    // on a replica. An instance admin holding no editor grant, and any
    // space-admin on a replica, have canManageAccess WITHOUT canEdit — and they
    // are precisely the audience for the permissions screen. Gating it on
    // canEdit would have locked out exactly those people.
    renderPage({ canEdit: false, canManageAccess: true })
    expect(await screen.findByRole('link', { name: 'Permissions' })).toHaveAttribute(
      'href',
      '/pages/page-1/permissions',
    )
    // Still no editing affordances — the two rights are separate.
    expect(screen.queryByRole('button', { name: 'Move page' })).not.toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Value for Owner' })).not.toBeInTheDocument()
  })

  it('offers no Permissions entry without canManageAccess, even to an editor', async () => {
    renderPage({ canEdit: true, canManageAccess: false })
    await screen.findByRole('button', { name: 'Move page' })
    expect(screen.queryByRole('link', { name: 'Permissions' })).not.toBeInTheDocument()
  })

  it('gives an editor a value field, a remove button and the add form', async () => {
    renderPage()
    expect(await screen.findByRole('textbox', { name: 'Value for Owner' })).toHaveValue('Ada Lovelace')
    expect(screen.getByRole('button', { name: 'Remove Status' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Add a property' })).toBeInTheDocument()
  })

  it('stays blurry about absent-vs-not-yours when the page does not resolve (§6.7)', async () => {
    renderPage({ pageMissing: true })
    expect(await screen.findByText("Couldn't load this page.")).toBeInTheDocument()
  })
})

describe('PageDetailsPage clearing a value', () => {
  it('removes the property instead of setting an empty value', async () => {
    const mock = renderPage()
    const field = await screen.findByRole('textbox', { name: 'Value for Status' })
    fireEvent.change(field, { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(mutations(mock, 'RemovePageProperty')).toHaveLength(1))
    expect(mutations(mock, 'RemovePageProperty')[0]).toEqual({
      input: { pageId: 'page-1', pagePropertyKeyId: 'k-status' },
    })
    // The refused gesture never happens: no set with an empty value.
    expect(mutations(mock, 'SetPageProperty')).toHaveLength(0)
    expect(screen.queryByText(/value cannot be empty/i)).not.toBeInTheDocument()
  })

  it('treats a whitespace-only value the same way', async () => {
    const mock = renderPage()
    const field = await screen.findByRole('textbox', { name: 'Value for Status' })
    fireEvent.change(field, { target: { value: '   ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(mutations(mock, 'RemovePageProperty')).toHaveLength(1))
    expect(mutations(mock, 'SetPageProperty')).toHaveLength(0)
  })

  it('keeps Save disabled until something actually changes', async () => {
    renderPage()
    const save = await screen.findByRole('button', { name: 'Save changes' })
    expect(save).toBeDisabled()
    fireEvent.change(screen.getByRole('textbox', { name: 'Value for Owner' }), { target: { value: 'Grace Hopper' } })
    expect(save).toBeEnabled()
  })
})

describe('PageDetailsPage saving and adding', () => {
  it('sends only the rows that changed', async () => {
    const mock = renderPage()
    const field = await screen.findByRole('textbox', { name: 'Value for Owner' })
    fireEvent.change(field, { target: { value: 'Grace Hopper' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(mutations(mock, 'SetPageProperty')).toHaveLength(1))
    expect(mutations(mock, 'SetPageProperty')[0]).toEqual({
      input: { pageId: 'page-1', pagePropertyKeyId: 'k-owner', value: 'Grace Hopper' },
    })
    expect(mutations(mock, 'RemovePageProperty')).toHaveLength(0)
  })

  it('removes a single row from its own button', async () => {
    const mock = renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Remove Status' }))
    await waitFor(() => expect(mutations(mock, 'RemovePageProperty')).toHaveLength(1))
    expect(mutations(mock, 'RemovePageProperty')[0]).toEqual({
      input: { pageId: 'page-1', pagePropertyKeyId: 'k-status' },
    })
  })

  it('adds a property from the registry picker, offering only keys the page lacks', async () => {
    const mock = renderPage()
    const picker = await screen.findByRole('combobox', { name: 'Key' })
    fireEvent.mouseDown(picker)
    // Owner and Status are already on the page — offering them again would
    // silently overwrite the rows above.
    expect(screen.getAllByRole('option')).toHaveLength(1)
    fireEvent.click(screen.getByRole('option', { name: 'Review Date' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Value' }), { target: { value: '2026-11-01' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add property' }))

    await waitFor(() => expect(mutations(mock, 'SetPageProperty')).toHaveLength(1))
    expect(mutations(mock, 'SetPageProperty')[0]).toEqual({
      input: { pageId: 'page-1', pagePropertyKeyId: 'k-review', value: '2026-11-01' },
    })
  })

  it('never offers Add with an empty value — the server refuses it', async () => {
    renderPage()
    const picker = await screen.findByRole('combobox', { name: 'Key' })
    fireEvent.mouseDown(picker)
    fireEvent.click(screen.getByRole('option', { name: 'Review Date' }))
    expect(screen.getByRole('button', { name: 'Add property' })).toBeDisabled()
    fireEvent.change(screen.getByRole('textbox', { name: 'Value' }), { target: { value: '  ' } })
    expect(screen.getByRole('button', { name: 'Add property' })).toBeDisabled()
  })
})

describe('PageDetailsPage typed refusals', () => {
  it('explains a read-only replica instead of showing a raw error (design.md §12)', async () => {
    const mock = renderPage({
      setError: { kind: 'ReadOnlyReplica', spaceId: 'space-1', originInstanceId: 'LOW', message: null },
    })
    fireEvent.change(await screen.findByRole('textbox', { name: 'Value for Owner' }), {
      target: { value: 'Grace Hopper' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('This space is read-only')).toBeInTheDocument()
    expect(screen.getByText(/Replica spaces are always read-only/)).toBeInTheDocument()
    expect(mutations(mock, 'SetPageProperty')).toHaveLength(1)
  })

  it('says why a replica page is read-only before anything is attempted (design.md §12)', async () => {
    renderPage({ isReplica: true, canEdit: false })
    expect(await screen.findByText(/Replica of LOW — read-only/)).toBeInTheDocument()
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
  })

  it('shows a Forbidden refusal inline rather than swallowing it', async () => {
    renderPage({ removeError: { kind: 'Forbidden', message: 'canEdit required' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Remove Status' }))
    expect(await screen.findByText('Not permitted: canEdit required')).toBeInTheDocument()
  })
})

describe('PageDetailsPage protective marking section (design.md §21)', () => {
  it('puts the marking in its own section ABOVE the properties table, never as a row', async () => {
    renderPage()
    const marking = await screen.findByRole('heading', { name: 'Protective marking' })
    const properties = screen.getByRole('heading', { name: 'Properties' })
    // A property is metadata beside the page (§20); a marking decides who may
    // read the page at all (§21), and the layout has to say which is which.
    expect(marking.compareDocumentPosition(properties) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    const table = screen.getByRole('table', { name: 'Page properties' })
    expect(table.textContent).not.toContain('OFFICIAL')
  })

  it('shows a non-editor the marking but no control to change it', async () => {
    // The marking itself is not hidden from anyone who can view the page; §21's
    // banners carry it. What a non-editor does not get is the means to CHANGE it.
    renderPage({ canEdit: false })
    await screen.findByRole('table', { name: 'Page properties' })
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument()
  })
})

describe('PageDetailsPage empty states', () => {
  it('names the registry as the missing piece when no keys are defined', async () => {
    renderPage({ properties: [], registry: [] })
    expect(
      await screen.findByText('No properties yet — an instance admin defines the available keys.'),
    ).toBeInTheDocument()
  })

  it('points an editor at the add form when keys exist but the page has none', async () => {
    renderPage({ properties: [] })
    expect(await screen.findByText('No properties yet — pick a key below to add the first one.')).toBeInTheDocument()
  })

  it('tells a non-editor why the page carries no properties', async () => {
    renderPage({ properties: [], canEdit: false })
    expect(
      await screen.findByText('No properties on this page — only someone who can edit it can add them.'),
    ).toBeInTheDocument()
  })

  it('says something useful when there is nothing on the screen at all', async () => {
    // No properties, no edit rights, no access rights: a bare header over blank
    // space would read as a broken screen.
    renderPage({ properties: [], canEdit: false, canManageAccess: false })
    expect(await screen.findByText(/Nothing is kept beside this page yet/)).toBeInTheDocument()
  })

  it('says the space is a replica rather than calling its editors non-editors', async () => {
    // On a replica canEdit is false for EVERYONE (§12 refuses beneath every grant),
    // so a bare canEdit gate would tell a space's own editors they are not editors -
    // and swallow the one screen that explains why the space is read-only.
    renderPage({ canEdit: false, isReplica: true })
    expect(await screen.findByText(/Replica of LOW/)).toBeInTheDocument()
    // The screen renders read-only and the banner says WHY: "this is a replica"
    // and "you are not an editor" are different facts.
    expect(screen.queryByText(/managed by its editors/)).not.toBeInTheDocument()
    expect(screen.getByRole('table', { name: 'Page properties' })).toBeInTheDocument()
  })
})
