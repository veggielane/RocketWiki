import type { ClassificationLevel } from '../graphql/generated/graphql'
import type { ThemeMode } from '../theme/theme'

/**
 * The visual accent for each classification (design.md §21.1's fixed ladder).
 *
 * **Colour is an accent here, never a signal.** WCAG 1.4.1 forbids colour as
 * the only carrier of information, and a protective marking is the last place
 * that could be tolerated: a monochrome printout and a screen reader must
 * carry exactly what the screen carries. Every surface using this table
 * renders the marking's TEXT — `label` on a banner, the level's wire name on
 * a list badge — and the tone only makes a higher classification louder
 * beside it. Nothing is encoded in the colour that is not already written.
 *
 * Every pair is an explicit foreground on an explicit opaque background
 * rather than a theme token over a tint: the CI a11y tier measures
 * `color-contrast` on real painted pixels, and a translucent fill leaves the
 * measured value depending on whatever happens to sit behind it. Measured
 * ratios (sRGB, WCAG 2.x), all well clear of the 4.5:1 the smallest of this
 * text needs:
 *
 * | Level              | Light            | Dark             |
 * |--------------------|------------------|------------------|
 * | OFFICIAL           | 11.92:1          | 11.04:1          |
 * | OFFICIAL_SENSITIVE | 10.29:1          | 10.36:1          |
 * | SECRET             |  6.57:1          |  8.82:1          |
 * | TOP_SECRET         | 13.77:1          | 15.53:1          |
 */
export interface MarkingTone {
  bg: string
  fg: string
  border: string
  /** The louder half of the ladder gets a heavier rule — a second, non-colour loudness cue. */
  borderWidth: number
}

const LIGHT: Record<ClassificationLevel, MarkingTone> = {
  OFFICIAL: { bg: '#e3e8ef', fg: '#1f2937', border: '#64748b', borderWidth: 1 },
  OFFICIAL_SENSITIVE: { bg: '#fdecc8', fg: '#4a3208', border: '#8a5a05', borderWidth: 1 },
  SECRET: { bg: '#b02a1f', fg: '#ffffff', border: '#7e1d15', borderWidth: 2 },
  TOP_SECRET: { bg: '#5b0f16', fg: '#ffffff', border: '#f0b429', borderWidth: 3 },
}

const DARK: Record<ClassificationLevel, MarkingTone> = {
  OFFICIAL: { bg: '#232b36', fg: '#dbe3ee', border: '#6b7a90', borderWidth: 1 },
  OFFICIAL_SENSITIVE: { bg: '#3a2c10', fg: '#f6dfa8', border: '#c9992e', borderWidth: 1 },
  SECRET: { bg: '#8e2119', fg: '#ffffff', border: '#c4483c', borderWidth: 2 },
  TOP_SECRET: { bg: '#4a0c12', fg: '#ffffff', border: '#f0b429', borderWidth: 3 },
}

/**
 * A level the SPA cannot place falls back to the loudest tone rather than an
 * unstyled one, matching §21.3's asymmetry: an out-of-ladder level is treated
 * as TOP SECRET, because the quiet direction is the one that misrepresents
 * how protected the content is.
 */
export function markingTone(level: ClassificationLevel, mode: ThemeMode): MarkingTone {
  const table = mode === 'dark' ? DARK : LIGHT
  return table[level] ?? table.TOP_SECRET
}
