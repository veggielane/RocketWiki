import { describe, expect, it, vi } from 'vitest'
import { createDrawioSession } from '../drawioEmbed'
import { DRAWIO_MAX_PAYLOAD_CHARS } from '../drawioPayload'

/**
 * The embed handshake (init → load → save → export → exit), driven by a
 * scripted fake frame. NOTE this is the strongest verification the protocol
 * gets in this repo: no diagrams.net instance runs in this environment, so
 * the message shapes come from the drawio embed documentation, not from a
 * live handshake.
 */

const PAYLOAD = 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4='
const SVG_PREFIX = 'data:image/svg+xml;base64,'

function fakeFrame() {
  const posted: Record<string, unknown>[] = []
  return { posted, post: (message: Record<string, unknown>) => posted.push(message) }
}

function fakeHandlers() {
  return {
    onSave: vi.fn<(payloadBase64: string) => void>(),
    onExit: vi.fn<() => void>(),
    onPayloadTooLarge: vi.fn<(payloadChars: number) => void>(),
    onProtocolError: vi.fn<(message: string) => void>(),
  }
}

const msg = (payload: Record<string, unknown>) => JSON.stringify(payload)

describe('drawio embed protocol session', () => {
  it('init with an existing diagram loads the stored payload as an SVG data URI, autosave off', () => {
    const frame = fakeFrame()
    const session = createDrawioSession(frame, PAYLOAD, fakeHandlers())
    session.handleMessage(msg({ event: 'init' }))
    expect(frame.posted).toEqual([{ action: 'load', xml: `${SVG_PREFIX}${PAYLOAD}`, autosave: 0 }])
  })

  it('init with no payload loads an empty document', () => {
    const frame = fakeFrame()
    const session = createDrawioSession(frame, '', fakeHandlers())
    session.handleMessage(msg({ event: 'init' }))
    expect(frame.posted).toEqual([{ action: 'load', xml: '', autosave: 0 }])
  })

  it('save requests an xmlsvg export instead of trusting the save event XML', () => {
    const frame = fakeFrame()
    const session = createDrawioSession(frame, PAYLOAD, fakeHandlers())
    session.handleMessage(msg({ event: 'save', xml: '<mxfile>…</mxfile>', exit: true }))
    expect(frame.posted).toEqual([{ action: 'export', format: 'xmlsvg' }])
  })

  it('a valid export hands the base64 payload to onSave', () => {
    const frame = fakeFrame()
    const handlers = fakeHandlers()
    const session = createDrawioSession(frame, '', handlers)
    session.handleMessage(msg({ event: 'export', format: 'xmlsvg', data: `${SVG_PREFIX}${PAYLOAD}` }))
    expect(handlers.onSave).toHaveBeenCalledExactlyOnceWith(PAYLOAD)
    expect(handlers.onPayloadTooLarge).not.toHaveBeenCalled()
    expect(handlers.onProtocolError).not.toHaveBeenCalled()
  })

  it('an oversized export is refused — onPayloadTooLarge fires, nothing is saved', () => {
    const handlers = fakeHandlers()
    const session = createDrawioSession(fakeFrame(), '', handlers)
    const oversized = 'A'.repeat(DRAWIO_MAX_PAYLOAD_CHARS + 1)
    session.handleMessage(msg({ event: 'export', data: `${SVG_PREFIX}${oversized}` }))
    expect(handlers.onPayloadTooLarge).toHaveBeenCalledExactlyOnceWith(DRAWIO_MAX_PAYLOAD_CHARS + 1)
    expect(handlers.onSave).not.toHaveBeenCalled()
  })

  it('a payload exactly at the cap is accepted', () => {
    const handlers = fakeHandlers()
    const session = createDrawioSession(fakeFrame(), '', handlers)
    const atCap = 'A'.repeat(DRAWIO_MAX_PAYLOAD_CHARS)
    session.handleMessage(msg({ event: 'export', data: `${SVG_PREFIX}${atCap}` }))
    expect(handlers.onSave).toHaveBeenCalledExactlyOnceWith(atCap)
  })

  it('an export that is not a base64 SVG data URI is a protocol error, not a save', () => {
    const handlers = fakeHandlers()
    const session = createDrawioSession(fakeFrame(), '', handlers)
    session.handleMessage(msg({ event: 'export', data: 'data:image/png;base64,AAAA' }))
    expect(handlers.onProtocolError).toHaveBeenCalledOnce()
    expect(handlers.onSave).not.toHaveBeenCalled()
  })

  it('exit reaches onExit', () => {
    const handlers = fakeHandlers()
    const session = createDrawioSession(fakeFrame(), '', handlers)
    session.handleMessage(msg({ event: 'exit', modified: false }))
    expect(handlers.onExit).toHaveBeenCalledOnce()
  })

  it('ignores non-protocol window traffic: objects, invalid JSON, unknown events', () => {
    const frame = fakeFrame()
    const handlers = fakeHandlers()
    const session = createDrawioSession(frame, '', handlers)
    session.handleMessage({ source: 'react-devtools' })
    session.handleMessage('not json at all')
    session.handleMessage(msg({ event: 'autosave', xml: '<mxfile/>' }))
    session.handleMessage(msg({ something: 'else' }))
    session.handleMessage(null)
    expect(frame.posted).toEqual([])
    expect(handlers.onSave).not.toHaveBeenCalled()
    expect(handlers.onExit).not.toHaveBeenCalled()
    expect(handlers.onProtocolError).not.toHaveBeenCalled()
  })

  it('full scripted lifecycle: init → load, save → export request, export → onSave', () => {
    const frame = fakeFrame()
    const handlers = fakeHandlers()
    const session = createDrawioSession(frame, PAYLOAD, handlers)

    session.handleMessage(msg({ event: 'init' }))
    expect(frame.posted).toHaveLength(1)
    expect(frame.posted[0]).toMatchObject({ action: 'load' })

    session.handleMessage(msg({ event: 'save', exit: true }))
    expect(frame.posted).toHaveLength(2)
    expect(frame.posted[1]).toEqual({ action: 'export', format: 'xmlsvg' })

    const updated = btoa('<svg xmlns="http://www.w3.org/2000/svg"><circle r="5"/></svg>')
    session.handleMessage(msg({ event: 'export', format: 'xmlsvg', data: `${SVG_PREFIX}${updated}` }))
    expect(handlers.onSave).toHaveBeenCalledExactlyOnceWith(updated)
  })
})
