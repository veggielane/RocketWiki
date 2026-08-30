import { Box, type BoxProps } from '@mui/material'
import type { ReactNode } from 'react'
import { PresencePointers } from './PresencePointers'
import type { PointerPosition } from '../realtime/types'

export interface PresenceSurfaceProps {
  /** Latest position per user id, from `usePresence`. */
  pointers: Map<string, PointerPosition>
  /** `usePresence`'s throttled recorder — safe to call on every raw mousemove. */
  recordPointer: (x: number, y: number) => void
  children: ReactNode
  sx?: BoxProps['sx']
}

/**
 * The region a page's live cursors are captured over and drawn on.
 *
 * One component rather than two copies, because the capture box and the
 * overlay have to be the SAME box: pointer positions are fractions of the
 * capturing element's rect, so an overlay positioned against anything else
 * would draw every remote cursor in the wrong place. They were previously
 * hand-paired in PageViewPage and PageEditPage, which is two chances to drift
 * and no way for either to notice.
 *
 * **What the fractions are of.** Both pages wrap their whole main region, so
 * `x` is a fraction of the content column — the same measure on both screens,
 * because the shell gives the column one width. Two people READING the same
 * page therefore see each other's cursor exactly where it is. `y` is a
 * fraction of that screen's own content height, and the view and edit screens
 * are not the same height (an editor has a toolbar and no comments), so
 * between a reader and an editor `y` means "this far down the page I am
 * looking at" rather than "this exact line". Translating between the two would
 * need a shared document coordinate system that neither screen has, for a
 * cursor that is a 16px arrow: the proportional answer is the honest one, and
 * it degrades gracefully.
 */
export function PresenceSurface({ pointers, recordPointer, children, sx }: PresenceSurfaceProps) {
  return (
    <Box
      sx={{ position: 'relative', ...sx }}
      onMouseMove={(event) => {
        // `currentTarget`, never `target`: the fractions must be of this
        // surface, not of whichever child element the pointer happens to be
        // over.
        const rect = event.currentTarget.getBoundingClientRect()
        // A collapsed box would divide by zero and broadcast Infinity to
        // everyone else on the page.
        if (rect.width === 0 || rect.height === 0) return
        recordPointer((event.clientX - rect.left) / rect.width, (event.clientY - rect.top) / rect.height)
      }}
    >
      {children}
      <PresencePointers pointers={pointers} />
    </Box>
  )
}
