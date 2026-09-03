import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import type { ReactElement } from 'react'
import { UserLink } from '../UserLink'
import { profilePath } from '../profilePath'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The one way a person is named on a screen. What has to hold: the name goes
 * to the profile by the LOCAL id every `UserRef` carries; a face beside it is
 * decorative, so the link is announced by the name once rather than twice;
 * and the id itself never reaches the page.
 */
const ada = { id: 'u-1', displayName: 'Ada Lovelace', hasAvatar: false }

const renderLink = (ui: ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>)

describe('UserLink', () => {
  it('links the display name to the profile route by local id', () => {
    renderLink(<UserLink user={ada} />)
    expect(screen.getByRole('link', { name: 'Ada Lovelace' })).toHaveAttribute('href', '/users/u-1')
  })

  it('shows a face when asked, without letting it name the link a second time', () => {
    renderLink(<UserLink user={ada} avatarSize={24} />)
    // The initials chip is there…
    expect(screen.getByText('A')).toBeInTheDocument()
    // …but hidden from assistive tech: the link is "Ada Lovelace", not "Ada
    // Lovelace Ada Lovelace", and no separate image is announced.
    expect(screen.getByRole('link', { name: 'Ada Lovelace' })).toBeInTheDocument()
    expect(screen.queryByRole('img', { name: 'Ada Lovelace' })).not.toBeInTheDocument()
  })

  it('renders no face unless a size is given', () => {
    const { container } = renderLink(<UserLink user={ada} />)
    expect(screen.queryByText('A')).not.toBeInTheDocument()
    expect(container.querySelector('.MuiAvatar-root')).toBeNull()
  })

  it('never renders the raw id', () => {
    const { container } = renderLink(<UserLink user={ada} avatarSize={24} />)
    expect(container.textContent).not.toContain('u-1')
  })

  it('escapes the id into the path', () => {
    // Ids are UUIDs in practice; the path must still be well-formed for any string.
    expect(profilePath('a b/c')).toBe('/users/a%20b%2Fc')
    expect(profilePath('7d2a1c9e-1111-2222-3333-444455556666')).toBe('/users/7d2a1c9e-1111-2222-3333-444455556666')
  })

  it('has no axe violations', async () => {
    // A div, not a p: the face is an Avatar (a div), which is why the
    // attachment list renders its secondary line as a div too.
    renderLink(
      <div>
        uploaded by <UserLink user={ada} avatarSize={16} />
      </div>,
    )
    await expectNoAxeViolations()
  })
})
