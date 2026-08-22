import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider, createClient, fetchExchange } from 'urql'
import { CreateSpacePage } from '../CreateSpacePage'

// No live API in this environment — queries/mutations simply won't
// resolve, which is fine here: these tests only assert the form's own
// client-side gating logic (design.md §6.5.1), not server behavior.
const client = createClient({ url: '/graphql', exchanges: [fetchExchange] })

function renderPage() {
  return render(
    <MemoryRouter>
      <UrqlProvider value={client}>
        <CreateSpacePage />
      </UrqlProvider>
    </MemoryRouter>,
  )
}

/**
 * design.md §6.5.1: space creation is atomic with its first grant, which
 * is required with no default. These tests exist to make "cannot be
 * skipped, cannot default to everyone" a property of the code, not just a
 * comment someone could accidentally undo.
 */
describe('CreateSpacePage — required initial grant, no default', () => {
  it('the role selector has no pre-selected value on mount', () => {
    renderPage()
    const roleSelect = screen.getByRole('combobox', { name: 'Initial grant role' })
    expect(within(roleSelect).getByText('Choose a role…')).toBeInTheDocument()
  })

  it('stays disabled until key, name, role, and a valid rule are all present', () => {
    renderPage()
    const createButton = screen.getByRole('button', { name: 'Create space' })
    expect(createButton).toBeDisabled()

    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'ENG' } })
    expect(createButton).toBeDisabled()

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Engineering' } })
    // Role and grant are still unset — still disabled even with key+name filled.
    expect(createButton).toBeDisabled()
  })

  it('never offers a "skip" affordance for the initial grant', () => {
    renderPage()
    expect(screen.queryByRole('button', { name: /skip/i })).not.toBeInTheDocument()
  })

  it('explains why the initial grant is required rather than silently blocking submission', () => {
    renderPage()
    expect(screen.getByText(/no default like "everyone"/i)).toBeInTheDocument()
  })
})
