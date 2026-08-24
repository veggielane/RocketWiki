import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { DrawioEditorDialog } from '../DrawioEditorDialog'
import type { DrawioConfig } from '../drawioConfig'

const PAYLOAD = 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4='
const INTERNAL: DrawioConfig = { url: 'http://drawio.internal:8080/', publicService: false }
const PUBLIC: DrawioConfig = { url: 'https://embed.diagrams.net/', publicService: true }

function renderDialog(overrides: Partial<Parameters<typeof DrawioEditorDialog>[0]> = {}) {
  const onSave = vi.fn()
  const onClose = vi.fn()
  render(
    <DrawioEditorDialog
      open
      payload={PAYLOAD}
      alt=""
      onSave={onSave}
      onClose={onClose}
      config={INTERNAL}
      {...overrides}
    />,
  )
  return { onSave, onClose }
}

describe('DrawioEditorDialog — config gate (design.md §15, fail closed)', () => {
  it('with no configured URL: instructive copy, and no iframe pointing anywhere', () => {
    renderDialog({ config: null })
    expect(screen.getByText('No diagram editor configured')).toBeInTheDocument()
    expect(screen.getAllByText(/VITE_DRAWIO_URL/).length).toBeGreaterThan(0)
    expect(screen.queryByTitle('draw.io diagram editor')).not.toBeInTheDocument()
  })

  it('with an in-network URL: iframe with the JSON embed-protocol switches, no external warning', () => {
    renderDialog()
    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')
    const src = new URL(iframe.src)
    expect(src.origin).toBe('http://drawio.internal:8080')
    expect(src.searchParams.get('embed')).toBe('1')
    expect(src.searchParams.get('proto')).toBe('json')
    expect(screen.queryByText(/External editor/)).not.toBeInTheDocument()
  })

  it('with the public service: a visible external-editor warning', () => {
    renderDialog({ config: PUBLIC })
    expect(screen.getByText(/External editor/)).toBeInTheDocument()
    expect(screen.getByText('embed.diagrams.net')).toBeInTheDocument()
  })
})

function deliver(iframe: HTMLIFrameElement, data: unknown, origin = 'http://drawio.internal:8080') {
  window.dispatchEvent(
    new MessageEvent('message', { data, origin, source: iframe.contentWindow ?? undefined }),
  )
}

describe('DrawioEditorDialog — message wiring around the protocol session', () => {
  it('answers the init event with a load of the stored payload, to the configured origin only', async () => {
    renderDialog()
    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')
    const postMessage = vi.spyOn(iframe.contentWindow!, 'postMessage')

    deliver(iframe, JSON.stringify({ event: 'init' }))
    await waitFor(() => expect(postMessage).toHaveBeenCalledOnce())
    const [raw, targetOrigin] = postMessage.mock.calls[0] as [string, string]
    expect(JSON.parse(raw)).toEqual({
      action: 'load',
      xml: `data:image/svg+xml;base64,${PAYLOAD}`,
      autosave: 0,
    })
    expect(targetOrigin).toBe('http://drawio.internal:8080')
  })

  it('ignores messages from a different origin — another frame cannot drive the session', async () => {
    renderDialog()
    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')
    const postMessage = vi.spyOn(iframe.contentWindow!, 'postMessage')

    deliver(iframe, JSON.stringify({ event: 'init' }), 'https://evil.example')
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(postMessage).not.toHaveBeenCalled()
  })

  it('a valid export saves the new payload', async () => {
    const { onSave } = renderDialog()
    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')
    const updated = btoa('<svg xmlns="http://www.w3.org/2000/svg"><circle r="5"/></svg>')

    deliver(iframe, JSON.stringify({ event: 'export', format: 'xmlsvg', data: `data:image/svg+xml;base64,${updated}` }))
    await waitFor(() => expect(onSave).toHaveBeenCalledExactlyOnceWith(updated, ''))
  })

  it('the alt text field rides along with the export, whitespace-normalized for the single-line Markdown form', async () => {
    const { onSave } = renderDialog({ alt: 'old description' })
    const field = screen.getByLabelText('Diagram description (alt text)')
    expect(field).toHaveValue('old description')
    // A single-line <input> already strips pasted newlines; edge/internal
    // whitespace runs are what can still reach the save path.
    fireEvent.change(field, { target: { value: '  Feed system:   tanks to combustion chamber  ' } })

    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')
    deliver(iframe, JSON.stringify({ event: 'export', data: `data:image/svg+xml;base64,${PAYLOAD}` }))
    await waitFor(() =>
      expect(onSave).toHaveBeenCalledExactlyOnceWith(PAYLOAD, 'Feed system: tanks to combustion chamber'),
    )
  })

  it('an oversized export surfaces the size-cap refusal in the dialog and saves nothing', async () => {
    const { onSave, onClose } = renderDialog()
    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')
    const oversized = 'A'.repeat(512 * 1024 + 1)

    deliver(iframe, JSON.stringify({ event: 'export', data: `data:image/svg+xml;base64,${oversized}` }))
    expect(await screen.findByText(/per-diagram limit/)).toBeInTheDocument()
    expect(screen.getByText(/524 KB/)).toBeInTheDocument()
    expect(onSave).not.toHaveBeenCalled()
    expect(onClose).not.toHaveBeenCalled()
  })

  it('the exit event closes the dialog without saving', async () => {
    const { onSave, onClose } = renderDialog()
    const iframe = screen.getByTitle<HTMLIFrameElement>('draw.io diagram editor')

    deliver(iframe, JSON.stringify({ event: 'exit', modified: true }))
    await waitFor(() => expect(onClose).toHaveBeenCalledOnce())
    expect(onSave).not.toHaveBeenCalled()
  })
})
