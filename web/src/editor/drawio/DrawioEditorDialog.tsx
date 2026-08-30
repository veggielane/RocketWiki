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

  // Re-seeded on open, like the fence-insert dialogs. Today the only caller
  // mounts this conditionally, so mount-time state happens to be enough — but
  // that is the caller's accident, not this component's contract, and a second
  // caller that keeps it mounted would silently edit one diagram's alt text
  // into the next.
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) setAltDraft(alt)
  }

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
      // The dialog with the most to lose in the whole app: a stray click on the
      // 8px of backdrop around a 92vh paper would discard an entire diagram
      // editing session AND the alt draft, with no undo — the iframe holds the
      // only copy until Save and Exit. Escape and Cancel still close, because
      // both are deliberate acts.
      onClose={(_event, reason) => {
        if (reason === 'backdropClick') return
        onClose()
      }}
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
            // The two grants the embed protocol actually needs, and nothing
            // else. Everything this session does is JSON `postMessage`
            // (drawioEmbed.ts: init → load → save → export → exit, with
            // `autosave: 0`), so the editor never needs to navigate the top
            // frame, open a window, submit a form, or download a file — and a
            // compromised or misconfigured editor host held all four.
            //
            //   allow-scripts     — it is a JavaScript application.
            //   allow-same-origin — load-bearing twice over. draw.io needs its
            //     own origin for its storage and config, and an opaque-origin
            //     frame posts messages with `event.origin === "null"`, which
            //     the listener above rejects: without this the handshake never
            //     completes and the editor never loads at all.
            //
            // The usual caveat about `allow-scripts allow-same-origin` — that a
            // frame can reach up and delete its own sandbox attribute — needs
            // the framed document to be same-origin with THIS document. The
            // editor is an external host by construction (VITE_DRAWIO_URL, with
            // no default), so it cannot reach this DOM and the restriction
            // holds.
            sandbox="allow-scripts allow-same-origin"
            // An empty Permissions-Policy allowlist: no camera, microphone,
            // geolocation, clipboard-read or anything else, stated rather than
            // left to the cross-origin defaults.
            allow=""
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
