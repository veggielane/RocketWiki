import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { SettingsPage } from '../SettingsPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The GitLab token surface (design.md §18). The load-bearing properties:
 * the whole section is hidden when the instance has no GitLab configured
 * (fail-closed extends to affordances), and the token is write-only — it
 * leaves the field on submit and is never displayed back.
 */

const TOKEN = 'glpat-SECRET-token-material'

function renderPage({ configured = true, hasTokenInitially = false } = {}) {
  // Mutable server-side state so refetches after mutations see the change.
  let hasToken = hasTokenInitially
  const mock = createMockUrqlClient((name) => {
    if (name === 'GitLabStatus')
      return {
        gitlabStatus: {
          configured,
          baseUrl: configured ? 'https://gitlab.example.com' : null,
          viewerHasToken: hasToken,
        },
      }
    if (name === 'SetGitLabToken') {
      hasToken = true
      return { setGitLabToken: { tokenState: { hasToken: true }, error: null } }
    }
    if (name === 'ClearGitLabToken') {
      hasToken = false
      return { clearGitLabToken: { tokenState: { hasToken: false }, error: null } }
    }
    return undefined
  })
  const utils = render(
    <UrqlProvider value={mock.client}>
      <SettingsPage />
    </UrqlProvider>,
  )
  return { mock, ...utils }
}

describe('SettingsPage — hidden when unconfigured (§15/§18 fail-closed)', () => {
  it('shows no GitLab section, and no mention of GitLab at all', async () => {
    renderPage({ configured: false })
    // The profile-picture section (§19) is instance-independent and always
    // present; the GitLab section must be absent without a trace.
    expect(await screen.findByText('Profile picture')).toBeInTheDocument()
    expect(screen.queryByText(/GitLab/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/Personal access token/)).not.toBeInTheDocument()
  })
})

describe('SettingsPage — accessibility', () => {
  it('has no axe violations with the GitLab section present', async () => {
    renderPage({ configured: true, hasTokenInitially: true })
    await screen.findByText('Profile picture')
    await screen.findByLabelText('Personal access token')
    await expectNoAxeViolations()
  })
})

describe('SettingsPage — set token flow', () => {
  it('submits the token, clears the field, and never displays it back', async () => {
    const { mock, container } = renderPage()
    const field = await screen.findByLabelText('Personal access token')
    // Password field: even while typing, the value is not readable text.
    expect(field).toHaveAttribute('type', 'password')

    fireEvent.change(field, { target: { value: TOKEN } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('GitLab token saved.')).toBeInTheDocument()
    // The mutation carried the token…
    const op = mock.operations.find((o) => o.name === 'SetGitLabToken')
    expect(op?.variables).toEqual({ input: { token: TOKEN } })
    // …the field emptied on submit…
    expect(field).toHaveValue('')
    // …and the token material appears nowhere in the rendered page.
    expect(container.innerHTML).not.toContain(TOKEN)
    // Status now reports a saved token — as a boolean, nothing more.
    expect(await screen.findByText('Token saved')).toBeInTheDocument()
  })

  it('a typed error shows designed copy — with the field still cleared', async () => {
    const mock = createMockUrqlClient((name) => {
      if (name === 'GitLabStatus')
        return { gitlabStatus: { configured: true, baseUrl: 'https://gitlab.example.com', viewerHasToken: false } }
      if (name === 'SetGitLabToken')
        return {
          setGitLabToken: {
            tokenState: null,
            error: {
              kind: 'Validation',
              message: 'Token appears malformed.',
              expectedRevisionNumber: null,
              actualRevisionNumber: null,
              latestTitle: null,
              latestContent: null,
              spaceId: null,
              originInstanceId: null,
              blockedPageCount: null,
              notFoundId: null,
            },
          },
        }
      return undefined
    })
    const { container } = render(
      <UrqlProvider value={mock.client}>
        <SettingsPage />
      </UrqlProvider>,
    )
    const field = await screen.findByLabelText('Personal access token')
    fireEvent.change(field, { target: { value: TOKEN } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Token appears malformed.')).toBeInTheDocument()
    expect(field).toHaveValue('')
    expect(container.innerHTML).not.toContain(TOKEN)
  })

  it('the save button is disabled while the field is empty', async () => {
    renderPage()
    await screen.findByLabelText('Personal access token')
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
  })
})

describe('SettingsPage — clear token flow', () => {
  it('clears the token and flips the status back to "No token"', async () => {
    const { mock } = renderPage({ hasTokenInitially: true })
    expect(await screen.findByText('Token saved')).toBeInTheDocument()

    // Confirmed first, like every other destructive action in the app.
    fireEvent.click(screen.getByRole('button', { name: 'Clear token' }))
    const dialog = await screen.findByRole('dialog', { name: 'Clear your GitLab token?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Clear token' }))

    expect(await screen.findByText('GitLab token cleared.')).toBeInTheDocument()
    expect(mock.operations.map((o) => o.name)).toContain('ClearGitLabToken')
    await waitFor(() => expect(screen.getByText('No token')).toBeInTheDocument())
    // `waitFor`, because the dialog's own confirm button carries the same label
    // and lingers for the length of MUI's exit transition.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Clear token' })).not.toBeInTheDocument(),
    )
  })

  it('cancelling the confirmation clears nothing', async () => {
    const { mock } = renderPage({ hasTokenInitially: true })
    await screen.findByText('Token saved')

    fireEvent.click(screen.getByRole('button', { name: 'Clear token' }))
    const dialog = await screen.findByRole('dialog', { name: 'Clear your GitLab token?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    expect(mock.operations.map((o) => o.name)).not.toContain('ClearGitLabToken')
    expect(screen.getByText('Token saved')).toBeInTheDocument()
  })

  it('no clear button is offered when there is no token to clear', async () => {
    renderPage({ hasTokenInitially: false })
    await screen.findByLabelText('Personal access token')
    expect(screen.queryByRole('button', { name: 'Clear token' })).not.toBeInTheDocument()
  })
})
