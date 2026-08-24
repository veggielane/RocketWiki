import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { CreateSpacePage } from '../CreateSpacePage'
import { expectNoAxeViolations } from '../../test/axe'
import { createMockUrqlClient } from '../../test/mockUrqlClient'

// Every operation answered with no data: these tests assert the form's own
// client-side gating logic (design.md §6.5.1), never server behavior, so
// there is nothing to stage. Goes through the shared mock client like the
// rest of the suite rather than a real fetchExchange — a test client that
// actually reaches for the network is one flaky DNS lookup from failing
// for reasons that have nothing to do with the form.
function renderPage() {
  return render(
    <MemoryRouter>
      <UrqlProvider value={createMockUrqlClient(() => undefined).client}>
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

  it('has no axe violations (WCAG 2.2 AA policy, test/axe.ts)', async () => {
    renderPage()
    screen.getByRole('button', { name: 'Create space' })
    await expectNoAxeViolations()
  })
})
