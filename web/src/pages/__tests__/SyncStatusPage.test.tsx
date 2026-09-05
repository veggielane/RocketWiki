import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { SyncStatusPage } from '../SyncStatusPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The instance's Sync feature flag reaches the admin page as
 * `syncStatus.enabled` (docs/CONFIGURATION.md "Feature flags"). The page must
 * EXPLAIN a switched-off instance rather than pretend sync is running — and
 * must still show the durable state, because pending events on an instance
 * whose sync was just switched off are exactly what an admin needs to see.
 */

function renderPage({ enabled }: { enabled: boolean }) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'SyncStatus')
      return {
        syncStatus: {
          enabled,
          localInstanceId: 'standalone',
          exportedSpaces: [],
          origins: [],
        },
      }
    return undefined
  })
  return render(
    <UrqlProvider value={mock.client}>
      <MemoryRouter>
        <SyncStatusPage />
      </MemoryRouter>
    </UrqlProvider>,
  )
}

describe('SyncStatusPage — the Sync feature flag', () => {
  it('says nothing about the flag when sync is on', async () => {
    renderPage({ enabled: true })
    expect(await screen.findByText('No spaces are flagged for export from this instance.')).toBeInTheDocument()
    expect(screen.queryByText(/switched off on this instance/)).not.toBeInTheDocument()
  })

  it('explains a switched-off instance, and still reports the state beneath it', async () => {
    renderPage({ enabled: false })
    expect(await screen.findByText(/Low → high sync is switched off on this instance/)).toBeInTheDocument()
    // Explained, not hidden: the sections are still there.
    expect(screen.getByText('No spaces are flagged for export from this instance.')).toBeInTheDocument()
    expect(screen.getByText('No sync bundles have been imported into this instance.')).toBeInTheDocument()
    await expectNoAxeViolations()
  })
})
