import { useState } from 'react'
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { parseGitLabIssueUrl, type GitLabIssueRef } from '../../gitlab/issueScheme'
import { useDialogFullScreen } from '../../app/useDialogFullScreen'
import { InsertRequirements } from '../InsertRequirements'

export interface InsertGitLabIssueLinkDialogProps {
  open: boolean
  /** Pre-filled from the editor selection, if any. */
  initialText: string
  onClose: () => void
  onInsert: (ref: GitLabIssueRef, text: string) => void
}

/**
 * Insert affordance for `[text](gitlab-issue://{project}/{iid})` links
 * (design.md §18). Pasting a full GitLab issue URL into the first field is
 * the *explicit* paste-time conversion §18 permits: the host is stripped
 * and only project + iid survive — the stored form is scheme-only.
 */
export function InsertGitLabIssueLinkDialog({ open, initialText, onClose, onInsert }: InsertGitLabIssueLinkDialogProps) {
  const fullScreen = useDialogFullScreen()
  const [url, setUrl] = useState('')
  const [project, setProject] = useState('')
  const [iid, setIid] = useState('')
  const [text, setText] = useState(initialText)
  // Remount-per-open state reset, same render-time idiom as PageViewPage.
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) {
      setUrl('')
      setProject('')
      setIid('')
      setText(initialText)
    }
  }

  const handleUrlChange = (value: string) => {
    setUrl(value)
    const parsed = parseGitLabIssueUrl(value)
    if (parsed) {
      setProject(parsed.project)
      setIid(parsed.iid)
    }
  }

  const iidValid = /^\d+$/.test(iid)
  const missing = [
    ...(project.trim().length === 0 ? ['a project'] : []),
    ...(iidValid ? [] : [iid.length === 0 ? 'an issue number' : 'a numeric issue number']),
  ]
  const canInsert = missing.length === 0
  const effectiveText = text.trim().length > 0 ? text.trim() : `${project}#${iid}`

  const handleInsert = () => {
    if (!canInsert) return
    onInsert({ project: project.trim(), iid }, effectiveText)
    onClose()
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth fullScreen={fullScreen}>
      <DialogTitle>Insert GitLab issue link</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            autoFocus
            label="Paste a GitLab issue URL (optional)"
            value={url}
            onChange={(e) => handleUrlChange(e.target.value)}
            fullWidth
            size="small"
            helperText="Fills in project and issue number — only those are stored, never the host."
          />
          <TextField
            label="Project"
            value={project}
            onChange={(e) => setProject(e.target.value)}
            required
            fullWidth
            size="small"
            helperText="Numeric id or namespaced path, e.g. propulsion/turbopump"
          />
          <TextField
            label="Issue number"
            value={iid}
            onChange={(e) => setIid(e.target.value)}
            required
            fullWidth
            size="small"
            error={iid.length > 0 && !iidValid}
            // A helper slot that empties once the value is valid drops a line of
            // height and shifts every field below it mid-typing. Every other
            // field in the family keeps a fallback string; so does this one.
            helperText={iid.length > 0 && !iidValid ? 'Digits only (the issue iid).' : 'The issue iid, e.g. 42.'}
          />
          <TextField label="Link text" value={text} onChange={(e) => setText(e.target.value)} fullWidth size="small" />
          {/* Politely announced, like the page-list dialog's read-out: it is a
              live preview of what gets stored, and it changes as you type. */}
          <Typography variant="caption" color="text.secondary" aria-live="polite">
            The page stores gitlab-issue://{project.trim() || '{project}'}/{iidValid ? iid : '{iid}'} — live state
            renders when the page is viewed.
          </Typography>
          <InsertRequirements missing={missing} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" onClick={handleInsert} disabled={!canInsert}>
          Insert
        </Button>
      </DialogActions>
    </Dialog>
  )
}
