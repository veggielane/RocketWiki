/**
 * Contrast-guaranteed label text for an arbitrary accent colour (presence
 * pointers, co-edit caret labels, initials avatars). The presence colour is
 * server-assigned per user and arbitrary in hue — a fixed white label fails
 * WCAG 1.4.3 (4.5:1) on light hues (yellows/light greens at any saturation).
 *
 * For ANY background L, max(contrast(white), contrast(black)) ≥ √21 ≈ 4.58,
 * so picking the better of black/white always clears the AA floor — the
 * colour itself stays exactly what the server assigned (every viewer keeps
 * seeing the same identity colour), only the label text adapts.
 */
export function readableTextOn(colour: string): '#000000' | '#ffffff' {
  const rgb = parseCssColour(colour)
  if (!rgb) return '#ffffff' // unknown format — legacy behaviour
  const L = relativeLuminance(rgb)
  // Crossover where contrast(white) == contrast(black): (L+0.05)² = 1.05·0.05.
  return L > 0.1791 ? '#000000' : '#ffffff'
}

/** WCAG relative luminance of sRGB [0..255] channels. */
export function relativeLuminance([r, g, b]: [number, number, number]): number {
  const lin = (c: number) => {
    const s = c / 255
    return s <= 0.04045 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4)
  }
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)
}

/** WCAG contrast ratio between two sRGB colours (exported for tests). */
export function contrastRatio(a: [number, number, number], b: [number, number, number]): number {
  const la = relativeLuminance(a)
  const lb = relativeLuminance(b)
  const [hi, lo] = la >= lb ? [la, lb] : [lb, la]
  return (hi + 0.05) / (lo + 0.05)
}

/** Parses the colour forms this app actually uses: #rgb/#rrggbb, rgb(), hsl(). */
export function parseCssColour(colour: string): [number, number, number] | null {
  const c = colour.trim().toLowerCase()

  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/.exec(c)
  if (hex) {
    const h = hex[1]!
    if (h.length === 3) {
      return [parseInt(h[0]! + h[0]!, 16), parseInt(h[1]! + h[1]!, 16), parseInt(h[2]! + h[2]!, 16)]
    }
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)]
  }

  const rgb = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)/.exec(c)
  if (rgb) {
    return [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])]
  }

  const hsl = /^hsla?\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%/.exec(c)
  if (hsl) {
    return hslToRgb(Number(hsl[1]), Number(hsl[2]) / 100, Number(hsl[3]) / 100)
  }

  return null
}

function hslToRgb(h: number, s: number, l: number): [number, number, number] {
  const hue = ((h % 360) + 360) % 360
  const c = (1 - Math.abs(2 * l - 1)) * s
  const x = c * (1 - Math.abs(((hue / 60) % 2) - 1))
  const m = l - c / 2
  const [r, g, b] =
    hue < 60 ? [c, x, 0] : hue < 120 ? [x, c, 0] : hue < 180 ? [0, c, x] : hue < 240 ? [0, x, c] : hue < 300 ? [x, 0, c] : [c, 0, x]
  return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)]
}
