import { describe, expect, it } from 'vitest'
import { contrastRatio, parseCssColour, readableTextOn } from '../readableTextOn'
import { colourForUser } from '../colourForUser'

/**
 * The presence-colour contrast guarantee (WCAG 1.4.3): whatever colour the
 * server assigns, the label text chosen by readableTextOn must clear 4.5:1
 * against it. This is the "fix it properly" alternative to suppressing the
 * color-contrast rule for presence labels — see docs/ACCESSIBILITY.md.
 */
describe('readableTextOn', () => {
  it('picks black on light hues where white fails (yellow presence colour)', () => {
    expect(readableTextOn('hsl(60, 70%, 45%)')).toBe('#000000')
    expect(readableTextOn('#ffeb3b')).toBe('#000000')
  })

  it('picks white on dark colours', () => {
    expect(readableTextOn('hsl(240, 70%, 45%)')).toBe('#ffffff')
    expect(readableTextOn('#7b1fa2')).toBe('#ffffff')
    expect(readableTextOn('rgb(21, 101, 192)')).toBe('#ffffff')
  })

  it('guarantees ≥ 4.5:1 for every hue the colourForUser generator can emit', () => {
    // colourForUser output is hsl(hue, 70%, 45%) — sweep all 360 hues rather
    // than trusting a sample of user ids.
    for (let hue = 0; hue < 360; hue++) {
      const bg = parseCssColour(`hsl(${hue}, 70%, 45%)`)!
      const text = readableTextOn(`hsl(${hue}, 70%, 45%)`) === '#000000' ? [0, 0, 0] : [255, 255, 255]
      const ratio = contrastRatio(bg, text as [number, number, number])
      expect(ratio, `hue ${hue}`).toBeGreaterThanOrEqual(4.5)
    }
  })

  it('guarantees ≥ 4.5:1 for arbitrary RGB colours (server-assigned, format unknown)', () => {
    // Deterministic pseudo-random sweep across the RGB cube.
    for (let i = 0; i < 500; i++) {
      const r = (i * 97) % 256
      const g = (i * 193) % 256
      const b = (i * 61) % 256
      const text = readableTextOn(`rgb(${r}, ${g}, ${b})`) === '#000000' ? [0, 0, 0] : [255, 255, 255]
      const ratio = contrastRatio([r, g, b], text as [number, number, number])
      expect(ratio, `rgb(${r},${g},${b})`).toBeGreaterThanOrEqual(4.5)
    }
  })

  it('handles the real generator output end to end', () => {
    for (const id of ['user-ada', 'user-grace', 'user-chris', 'a', '']) {
      const colour = colourForUser(id)
      expect(parseCssColour(colour)).not.toBeNull()
      expect(['#000000', '#ffffff']).toContain(readableTextOn(colour))
    }
  })

  it('falls back to white for unparseable colours', () => {
    expect(readableTextOn('currentColor')).toBe('#ffffff')
    expect(readableTextOn('papayawhip')).toBe('#ffffff')
  })
})
