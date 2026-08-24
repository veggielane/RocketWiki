import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, TextField, Typography } from '@mui/material'
import { buildEmbedUrl, embedOrigin, readDrawioConfig, type DrawioConfig } from './drawioConfig'
import { createDrawioSession } from './drawioEmbed'
import { DRAWIO_MAX_PAYLOAD_CHARS } from './drawioPayload'
import { formatBytes } from '../../attachments/formatBytes'

export interface DrawioEditorDialogProps {
  open: boolean
  /** Current base64 payload ('' for a new diagram). */
  payload: string
  /** Current alt text ('' for none). */
  alt: string
  /** Fires on the editor's Save and Exit, with the alt field as typed. */
  onSave: (payloadBase64: string, alt: string) => void
  onClose: () => void
  /**
   * Overridable for tests. `undefined` (the default) reads the build-time
   * environment; explicit `null` means "not configured".
   */
  config?: DrawioConfig | null
}

/**
 * The embedded diagrams.net editor, as a modal over the page. All protocol
 * logic lives in drawioEmbed.ts; this component owns the iframe and the
 * `message` listener, filtered by BOTH origin and source window so no other
 * frame or origin can inject protocol messages (the drawio payload is page
 * content, but the editor URL is config — treat its channel carefully).
 */
export function DrawioEditorDialog({ open, payload, alt, onSave, onClose, config }: DrawioEditorDialogProps) {
  const resolvedConfig = useMemo(
    () => (config !== undefined ? config : readDrawioConfig(import.meta.env as Record<string, unknown>)),
    [config],
  )
  const iframeRef = useRef<HTMLIFrameElement | null>(null)
  const [sessionError, setSessionError] = useState<string | null>(null)
  // The alt text field, saved together with the diagram on Save and Exit —
  // one save gesture for the whole edit, so Cancel discards both alike.
  const [altDraft, setAltDraft] = useState(alt)

  // Keep the latest callbacks reachable from the (per-open) session without
  // tearing the handshake down every parent re-render.
  const saveRef = useRef(onSave)
  saveRef.current = onSave
  const closeRef = useRef(onClose)
  closeRef.current = onClose
  const altDraftRef = useRef(altDraft)
  altDraftRef.current = altDraft

  const url = resolvedConfig?.url
  useEffect(() => {
    if (!open || !resolvedConfig) return
    setSessionError(null)
    const origin = embedOrigin(resolvedConfig)
    const session = createDrawioSession(
      {
        post: (message) => iframeRef.current?.contentWindow?.postMessage(JSON.stringify(message), origin),
      },
      payload,
      {
        // The alt draft rides along with the payload; trimmed because the
        // Markdown form is a single `alt: <text>` line (a stray newline or
        // edge whitespace would not survive the round trip byte-identically).
        onSave: (next) => saveRef.current(next, altDraftRef.current.replace(/\s+/g, ' ').trim()),
        onExit: () => closeRef.current(),
        onPayloadTooLarge: (chars) =>
          setSessionError(
            `This diagram exports to ${formatBytes(chars)}, over the ${formatBytes(
              DRAWIO_MAX_PAYLOAD_CHARS,
            )} per-diagram limit — it was not saved. Diagrams are stored inline in the page, so oversized ones make the page itself huge; split it up or simplify it, then save again.`,
          ),
        onProtocolError: (message) => setSessionError(message),
      },
    )
    const listener = (event: MessageEvent) => {
      if (event.origin !== origin) return
      if (!iframeRef.current || event.source !== iframeRef.current.contentWindow) return
      session.handleMessage(event.data)
    }
    window.addEventListener('message', listener)
    return () => window.removeEventListener('message', listener)
    // `payload` is deliberately captured per open: it's the document loaded
    // into the editor at handshake time, not something to hot-swap mid-edit.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, url])

  if (!resolvedConfig) {
    return (
      <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth aria-labelledby="drawio-dialog-title">
        <DialogTitle id="drawio-dialog-title">No diagram editor configured</DialogTitle>
        <DialogContent>
          <Alert severity="info" sx={{ mb: 2 }}>
            Existing diagrams still display — they are stored inline in the page. Only drawing and editing
            need the editor.
          </Alert>
          <Typography variant="body2" sx={{ mb: 1 }}>
            Editing diagrams needs a diagrams.net (draw.io) instance, and RocketWiki deliberately ships
            without a default: opening one means sending diagram content to whatever serves that URL, so —
            like the telemetry collector — it must be configured explicitly and sit inside the network
            boundary, never fall back to the public internet.
          </Typography>
          <Typography variant="body2">
            Set <code>VITE_DRAWIO_URL</code> to your in-network instance (the dev AppHost defines a{' '}
            <code>drawio</code> container for this). See <code>web/.env.example</code>.
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Close</Button>
        </DialogActions>
      </Dialog>
    )
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      maxWidth="xl"
      fullWidth
      aria-labelledby="drawio-dialog-title"
      slotProps={{ paper: { sx: { height: '92vh' } } }}
    >
      <DialogTitle id="drawio-dialog-title">Edit diagram</DialogTitle>
      <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 1, p: 1 }}>
        {resolvedConfig.publicService && (
          <Alert severity="warning">
            External editor: this editor is loaded from{' '}
            <strong>{new URL(resolvedConfig.url).hostname}</strong>, the public internet service, and
            diagram content is shared with it while you edit. Dev convenience only — production must set{' '}
            <code>VITE_DRAWIO_URL</code> to a self-hosted in-network instance.
          </Alert>
        )}
        {sessionError && (
          <Alert severity="error" onClose={() => setSessionError(null)}>
            {sessionError}
          </Alert>
        )}
        <TextField
          label="Diagram description (alt text)"
          value={altDraft}
          onChange={(event) => setAltDraft(event.target.value)}
          size="small"
          fullWidth
          helperText="Read by screen readers in place of the image. Saved with the diagram on Save and Exit."
        />
        {open && (
          <Box
            component="iframe"
            ref={iframeRef}
            src={buildEmbedUrl(resolvedConfig)}
            title="draw.io diagram editor"
            sx={{ border: 0, width: '100%', flexGrow: 1, minHeight: 0 }}
          />
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
      </DialogActions>
    </Dialog>
  )
}
