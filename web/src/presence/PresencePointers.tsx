import { Box } from '@mui/material'
import type { PresenceViewer } from '../realtime/types'

export interface PresencePointersProps {
  viewers: PresenceViewer[]
  pointers: Map<string, { x: number; y: number }>
}

/**
 * Absolutely-positioned overlay for other viewers' live cursors (design.md
 * §8). Must be rendered inside a `position: relative` container the same
 * size as the area pointer coordinates are relative to (see
 * PageViewPage.tsx) — positions are viewport-relative fractions (0..1),
 * not pixels, so they stay correct regardless of each viewer's window
 * size. `pointerEvents: 'none'` throughout: this overlay must never
 * intercept the real viewer's own mouse/click events.
 */
export function PresencePointers({ viewers, pointers }: PresencePointersProps) {
  const viewerById = new Map(viewers.map((v) => [v.connectionId, v]))

  return (
    <Box sx={{ position: 'absolute', inset: 0, pointerEvents: 'none', overflow: 'hidden', zIndex: 10 }}>
      {[...pointers.entries()].map(([connectionId, position]) => {
        const viewer = viewerById.get(connectionId)
        if (!viewer) return null
        return (
          <Box
            key={connectionId}
            sx={{
              position: 'absolute',
              left: `${position.x * 100}%`,
              top: `${position.y * 100}%`,
              transition: 'left 80ms linear, top 80ms linear',
            }}
          >
            <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
              <path d="M0 0 L0 14 L4 10 L7 16 L9 15 L6 9 L11 9 Z" fill={viewer.colour} />
            </svg>
            <Box
              component="span"
              sx={{
                display: 'inline-block',
                ml: 0.5,
                px: 0.5,
                borderRadius: 0.5,
                fontSize: '0.7rem',
                color: '#fff',
                bgcolor: viewer.colour,
                whiteSpace: 'nowrap',
              }}
            >
              {viewer.displayName}
            </Box>
          </Box>
        )
      })}
    </Box>
  )
}
