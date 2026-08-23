import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { ArchivedSpacesPage } from '../ArchivedSpacesPage'
import { createMockUrqlClient, type MockClient } from '../../test/mockUrqlClient'

const archived = [
  { id: 'space-1', key: 'ENG', name: 'Engineering', archivedAtUtc: '2026-07-01T12:00:00Z' },
  { id: 'space-2', key: 'OPS', name: 'Operations', archivedAtUtc: '2026-08-01T09:30:00Z' },
]

function renderPage(spaces: typeof archived): MockClient {
  const mock = createMockUrqlClient((name) => {
    if (name === 'ArchivedSpaces') return { archivedSpaces: spaces }
    if (name === 'RestoreSpace')
      return { restoreSpace: { space: { id: 'space-1', key: 'ENG', isDeleted: false }, error: null } }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <ArchivedSpacesPage />
    </UrqlProvider>,
  )
  return mock
}

describe('ArchivedSpacesPage', () => {
  it('lists archived spaces with key, name and archive date', async () => {
    renderPage(archived)
    expect(await screen.findByText('Engineering (ENG)')).toBeInTheDocument()
    expect(screen.getByText('Operations (OPS)')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Restore' })).toHaveLength(2)
  })

  it('states the scoped truth for an empty list — nothing YOU can restore, not "nothing archived"', async () => {
    renderPage([])
    expect(await screen.findByText('No archived spaces you can restore.')).toBeInTheDocument()
  })

  it('restores via the restoreSpace mutation with the space id', async () => {
    const mock = renderPage(archived)
    const buttons = await screen.findAllByRole('button', { name: 'Restore' })
    fireEvent.click(buttons[0]!)

    await waitFor(() => {
      const restores = mock.operations.filter((op) => op.name === 'RestoreSpace')
      expect(restores).toHaveLength(1)
      expect(restores[0]!.variables['input']).toEqual({ spaceId: 'space-1' })
    })
    expect(await screen.findByText('Restored "Engineering".')).toBeInTheDocument()
  })
})
