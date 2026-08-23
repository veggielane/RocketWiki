import { DRAWIO_MAX_PAYLOAD_CHARS } from './drawioPayload'

/**
 * The diagrams.net embed protocol (`embed=1&proto=json`), as pure logic.
 *
 * The lifecycle, per the drawio embed-mode documentation:
 *   1. iframe → `{event:'init'}` once the editor is ready
 *   2. host   → `{action:'load', xml}` — `xml` accepts an SVG data URI with
 *      embedded diagram XML (exactly our stored payload), or '' for a new
 *      diagram
 *   3. iframe → `{event:'save', …}` when the user hits Save and Exit
 *   4. host   → `{action:'export', format:'xmlsvg'}`
 *   5. iframe → `{event:'export', data:'data:image/svg+xml;base64,…', …}`
 *      → size-capped, then handed to `onSave`
 *   -  iframe → `{event:'exit'}` on Cancel/Exit → `onExit`
 *
 * This module never touches the DOM or `window` — the dialog component owns
 * the iframe, the `message` listener, and origin/source filtering, and
 * feeds raw `MessageEvent.data` in here. That keeps the whole handshake
 * drivable by a scripted fake frame in tests, which matters because no real
 * diagrams.net instance runs in this environment.
 */

/** Where outbound protocol messages go (the iframe's contentWindow, JSON-stringified, in production). */
export interface DrawioFrameTarget {
  post(message: Record<string, unknown>): void
}

export interface DrawioSessionHandlers {
  /** A valid, size-checked export arrived: the new base64 payload for the node. */
  onSave(payloadBase64: string): void
  /** The user exited the editor without saving. */
  onExit(): void
  /** Export exceeded DRAWIO_MAX_PAYLOAD_CHARS — nothing was saved; the editor stays open. */
  onPayloadTooLarge(payloadChars: number): void
  /** The editor answered with something other than a base64 SVG data URI. */
  onProtocolError(message: string): void
}

export interface DrawioSession {
  /** Feed a raw `MessageEvent.data` in. Non-protocol traffic is ignored. */
  handleMessage(raw: unknown): void
}

const SVG_DATA_URI_PREFIX = 'data:image/svg+xml;base64,'

export function createDrawioSession(
  frame: DrawioFrameTarget,
  initialPayloadBase64: string,
  handlers: DrawioSessionHandlers,
): DrawioSession {
  return {
    handleMessage(raw: unknown): void {
      // drawio sends JSON *strings*; other window traffic (devtools,
      // other libraries) is often structured-cloned objects — ignore it.
      if (typeof raw !== 'string') return
      let message: unknown
      try {
        message = JSON.parse(raw)
      } catch {
        return
      }
      if (typeof message !== 'object' || message === null) return
      const event = (message as { event?: unknown }).event

      if (event === 'init') {
        frame.post({
          action: 'load',
          xml: initialPayloadBase64.length > 0 ? `${SVG_DATA_URI_PREFIX}${initialPayloadBase64}` : '',
          autosave: 0,
        })
        return
      }

      if (event === 'save') {
        // The save event carries mxfile XML, but the stored format is the
        // editable SVG — ask for the real export instead of trusting it.
        frame.post({ action: 'export', format: 'xmlsvg' })
        return
      }

      if (event === 'export') {
        const data = (message as { data?: unknown }).data
        if (typeof data !== 'string' || !data.startsWith(SVG_DATA_URI_PREFIX)) {
          handlers.onProtocolError('The diagram editor returned an unexpected export format.')
          return
        }
        const payload = data.slice(SVG_DATA_URI_PREFIX.length)
        if (payload.length > DRAWIO_MAX_PAYLOAD_CHARS) {
          handlers.onPayloadTooLarge(payload.length)
          return
        }
        handlers.onSave(payload)
        return
      }

      if (event === 'exit') {
        handlers.onExit()
      }
      // Everything else (autosave, configure, …) is deliberately ignored.
    },
  }
}
