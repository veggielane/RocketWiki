import { describe, expect, it, vi } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import { useRealtimeConnection } from '../useRealtimeConnection'
import type { PresenceTransport, RealtimeConnectionState } from '../types'

/**
 * §1.8 — connection loss was invisible, and the UI actively asserted the
 * opposite.
 *
 * Both SignalR transports call `.withAutomaticReconnect()` and only ever
 * registered `onreconnected`, so the app could observe RECOVERY but never LOSS.
 * The edit screen therefore kept rendering a green "Live co-editing" chip right
 * through a disconnect, presence avatars froze at their last-known set with no
 * staleness cue, and the notification badge silently stopped updating.
 */

/** A transport that publishes connection state, so the hook can be driven. */
function makeTransport() {
  let emit: ((state: RealtimeConnectionState) => void) | null = null
  const transport: PresenceTransport = {
    joinRoom: vi.fn(async () => {}),
    leaveRoom: vi.fn(async () => {}),
    onViewersChanged: () => () => {},
    onReconnected: () => () => {},
    onConnectionStateChanged: (handler) => {
      emit = handler
      return () => {
        emit = null
      }
    },
  }
  return { transport, emit: (state: RealtimeConnectionState) => act(() => emit?.(state)) }
}

function Probe({ transport }: { transport: PresenceTransport }) {
  return <span data-testid="state">{useRealtimeConnection(transport)}</span>
}

describe('useRealtimeConnection', () => {
  it('starts connected, then reports loss and recovery', () => {
    const { transport, emit } = makeTransport()
    render(<Probe transport={transport} />)
    expect(screen.getByTestId('state')).toHaveTextContent('connected')

    emit('reconnecting')
    expect(screen.getByTestId('state')).toHaveTextContent('reconnecting')

    emit('disconnected')
    expect(screen.getByTestId('state')).toHaveTextContent('disconnected')

    emit('connected')
    expect(screen.getByTestId('state')).toHaveTextContent('connected')
  })

  it('treats a transport that publishes nothing as connected', () => {
    // The fakes and any future transport need not implement it, and the absence
    // of a signal must not make every test render a warning banner.
    const silent: PresenceTransport = {
      joinRoom: vi.fn(async () => {}),
      leaveRoom: vi.fn(async () => {}),
      onViewersChanged: () => () => {},
      onReconnected: () => () => {},
    }
    render(<Probe transport={silent} />)
    expect(screen.getByTestId('state')).toHaveTextContent('connected')
  })

  it('unsubscribes on unmount', () => {
    const unsubscribe = vi.fn()
    const transport: PresenceTransport = {
      joinRoom: vi.fn(async () => {}),
      leaveRoom: vi.fn(async () => {}),
      onViewersChanged: () => () => {},
      onReconnected: () => () => {},
      onConnectionStateChanged: () => unsubscribe,
    }
    const { unmount } = render(<Probe transport={transport} />)
    unmount()
    expect(unsubscribe).toHaveBeenCalled()
  })
})
