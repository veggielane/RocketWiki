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
  const canInsert = project.trim().length > 0 && iidValid
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
            label="Paste a GitLab issue URL (optional)"
            value={url}
            onChange={(e) => handleUrlChange(e.target.value)}
            fullWidth
            helperText="Fills in project and issue number — only those are stored, never the host."
          />
          <TextField
            label="Project"
            value={project}
            onChange={(e) => setProject(e.target.value)}
            required
            fullWidth
            helperText="Numeric id or namespaced path, e.g. propulsion/turbopump"
          />
          <TextField
            label="Issue number"
            value={iid}
            onChange={(e) => setIid(e.target.value)}
            required
            fullWidth
            error={iid.length > 0 && !iidValid}
            helperText={iid.length > 0 && !iidValid ? 'Digits only (the issue iid).' : undefined}
          />
          <TextField label="Link text" value={text} onChange={(e) => setText(e.target.value)} fullWidth />
          <Typography variant="caption" color="text.secondary">
            The page stores gitlab-issue://{project.trim() || '{project}'}/{iidValid ? iid : '{iid}'} — live state
            renders when the page is viewed.
          </Typography>
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
