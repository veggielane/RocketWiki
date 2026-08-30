import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { AuthNotConfigured } from '../AuthNotConfigured'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * What a build with no realm shows instead of redirecting to a guessed one.
 *
 * The screen exists because the alternative looked healthy: with the old
 * `http://localhost:8080/realms/rocketwiki` default an image built without the
 * build-arg came up fine, and the first symptom was users being sent to port
 * 8080 of their own machine at sign-in. This has to name the missing setting,
 * or it is just a nicer-looking dead end.
 */
describe('AuthNotConfigured', () => {
  it('names the setting that is missing', () => {
    render(<AuthNotConfigured />)
    expect(screen.getByText('VITE_OIDC_AUTHORITY')).toBeInTheDocument()
  })

  it('says the value is baked in at build time, not read at run time', () => {
    // Someone will otherwise go looking for a config file to edit on the
    // running container, and there isn't one.
    render(<AuthNotConfigured />)
    expect(screen.getByText(/baked in when the web app is built/)).toBeInTheDocument()
  })

  it('leads with a heading, so the page is navigable rather than a bare paragraph', () => {
    render(<AuthNotConfigured />)
    expect(screen.getByRole('heading', { level: 1, name: 'Sign-in is not configured' })).toBeInTheDocument()
  })

  it('has no axe violations — it may be the only screen a broken build ever shows', async () => {
    render(<AuthNotConfigured />)
    await expectNoAxeViolations()
  })
})
