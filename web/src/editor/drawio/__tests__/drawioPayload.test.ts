import { describe, expect, it } from 'vitest'
import { DRAWIO_MAX_PAYLOAD_CHARS, decodeDrawioPayload } from '../drawioPayload'

const SVG = '<svg xmlns="http://www.w3.org/2000/svg" content="&lt;mxfile&gt;&lt;/mxfile&gt;"><rect width="10" height="10"/></svg>'
const SVG_B64 = btoa(SVG)

describe('decodeDrawioPayload', () => {
  it('accepts base64 of an SVG and produces the <img>-ready data URI', () => {
    const result = decodeDrawioPayload(SVG_B64)
    expect(result).toEqual({ ok: true, dataUri: `data:image/svg+xml;base64,${SVG_B64}` })
  })

  it('accepts hand-wrapped (whitespace-containing) base64, compacting it for the data URI', () => {
    const wrapped = `${SVG_B64.slice(0, 20)}\n${SVG_B64.slice(20)}`
    const result = decodeDrawioPayload(wrapped)
    expect(result).toEqual({ ok: true, dataUri: `data:image/svg+xml;base64,${SVG_B64}` })
  })

  it('rejects text that is not base64', () => {
    const result = decodeDrawioPayload('not really base64!!')
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toMatch(/not valid base64/)
  })

  it('rejects base64 whose decoded bytes are not an SVG', () => {
    const result = decodeDrawioPayload(btoa('<mxfile>bare drawio xml, not the editable-SVG export</mxfile>'))
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toMatch(/not an SVG export/)
  })

  it('rejects an empty / whitespace-only payload', () => {
    expect(decodeDrawioPayload('   \n ').ok).toBe(false)
  })

  it('the per-diagram cap is 512 KB of base64 (the chars a diagram adds to the page markdown)', () => {
    expect(DRAWIO_MAX_PAYLOAD_CHARS).toBe(512 * 1024)
  })
})
