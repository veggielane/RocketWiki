import type { Theme } from '@mui/material/styles'
import type { ClassificationLevel } from '../graphql/generated/graphql'
import { markingTone } from '../markings/markingTone'

/**
 * What the canvases paint with, read off the app theme so a node drawn on
 * the light page and the same node on the dark one both come from the
 * palette that already passed the browser a11y tier. Every value is an
 * OPAQUE colour string: the 3D renderer hands them to three.js, which
 * ignores an alpha channel with a warning, so any translucency is applied
 * by the 2D canvas itself where it is wanted.
 *
 * Node fills are the classification tones (markings/markingTone.ts) — the
 * same colours the level badges in the legend beside the canvas wear, so
 * the legend explains the dots in the marking's own words. Colour is an
 * accent here as it is everywhere the tones are used: the table view carries
 * the level as text, and the focused node is told apart by shape, size and a
 * label, never by colour alone.
 */
export interface GraphColours {
  background: string
  /** Labels, the focus ring, and the links touching the focused page. */
  label: string
  /** Ordinary links, at full strength; the 2D canvas fades this itself. */
  link: string
  /** The gap ring inside the focus ring, so the ring reads as a ring on both themes. */
  focusRingGap: string
  nodeFill: (level: ClassificationLevel) => string
  nodeStroke: (level: ClassificationLevel) => string
}

export function graphColours(theme: Theme): GraphColours {
  const mode = theme.palette.mode === 'dark' ? 'dark' : 'light'
  return {
    background: theme.palette.background.default,
    label: theme.palette.text.primary,
    link: theme.palette.text.secondary,
    focusRingGap: theme.palette.background.default,
    nodeFill: (level) => markingTone(level, mode).bg,
    nodeStroke: (level) => markingTone(level, mode).border,
  }
}
