import { Box } from '@mui/material'
import { readableTextOn } from './readableTextOn'
import type { PointerPosition } from '../realtime/types'

export interface PresencePointersProps {
  /** Latest position per user id (usePresence keeps only the newest sample per viewer). */
  pointers: Map<string, PointerPosition>
}

/**
 * Absolutely-positioned overlay for other viewers' live cursors (design.md
 * §8). Must be rendered inside a `position: relative` container the same
 * size as the area pointer coordinates are relative to (see
 * PageViewPage.tsx) — positions are viewport-relative fractions (0..1),
 * not pixels, so they stay correct regardless of each viewer's window
 * size. Each hub sample carries the sender's display name + colour (and
 * nothing else — never attributes), so no join against the viewer list is
 * needed. `pointerEvents: 'none'` throughout: this overlay must never
 * intercept the real viewer's own mouse/click events.
 */
export function PresencePointers({ pointers }: PresencePointersProps) {
  // No overlay when nobody's pointing: a full-bleed positioned layer over
  // the page (even an empty, pointer-events:none one) also makes axe unable
  // to compute the background of everything underneath it, silently turning
  // the whole page's contrast checks into abstentions.
  if (pointers.size === 0) {
    return null
  }
  return (
    <Box
      // The WHOLE overlay is decorative. The cursor `<svg>` was already
      // hidden, but the name label beside it was not — so every remote viewer's
      // display name was live text in the accessibility tree, appearing and
      // disappearing inside the page's reading order as people moved a mouse.
      aria-hidden
      sx={{ position: 'absolute', inset: 0, pointerEvents: 'none', overflow: 'hidden', zIndex: 10 }}
    >
      {[...pointers.entries()].map(([userId, position]) => (
        <Box
          key={userId}
          sx={{
            position: 'absolute',
            left: `${position.x * 100}%`,
            top: `${position.y * 100}%`,
            // Smoothing between 20-per-second samples — but not for a reader
            // who has asked the OS for less motion. These are other people's
            // cursors gliding over text being read, which is exactly the
            // involuntary movement the preference exists to stop, and there is
            // no setting anywhere to turn presence cursors off.
            transition: 'left 80ms linear, top 80ms linear',
            '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
          }}
        >
          <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M0 0 L0 14 L4 10 L7 16 L9 15 L6 9 L11 9 Z" fill={position.colour} />
          </svg>
          <Box
            component="span"
            sx={{
              display: 'inline-block',
              ml: 0.5,
              px: 0.5,
              borderRadius: 0.5,
              fontSize: '0.7rem',
              // Server-assigned colour, arbitrary hue: label text must adapt
              // (black or white, whichever clears WCAG 1.4.3's 4.5:1) —
              // fixed white fails on light hues. See readableTextOn.ts.
              color: readableTextOn(position.colour),
              bgcolor: position.colour,
              whiteSpace: 'nowrap',
            }}
          >
            {position.displayName}
          </Box>
        </Box>
      ))}
    </Box>
  )
}
