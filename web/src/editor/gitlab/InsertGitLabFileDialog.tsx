import { useState } from 'react'
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField } from '@mui/material'
import type { GitLabFileRef } from '../../gitlab/fenceBody'
import { useDialogFullScreen } from '../../app/useDialogFullScreen'

export interface InsertGitLabFileDialogProps {
  open: boolean
  onClose: () => void
  onInsert: (ref: GitLabFileRef) => void
}

/**
 * Insert affordance for the ` ```gitlab-file ` fence (design.md §18):
 * project + path required, ref optional (server defaults to HEAD). The
 * dialog writes the canonical key=value body; the fence stays an ordinary
 * code block to the whole Markdown pipeline.
 */
export function InsertGitLabFileDialog({ open, onClose, onInsert }: InsertGitLabFileDialogProps) {
  const fullScreen = useDialogFullScreen()
  const [project, setProject] = useState('')
  const [path, setPath] = useState('')
  const [ref, setRef] = useState('')
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) {
      setProject('')
      setPath('')
      setRef('')
    }
  }

  const canInsert = project.trim().length > 0 && path.trim().length > 0

  const handleInsert = () => {
    if (!canInsert) return
    const trimmedRef = ref.trim()
    onInsert({
      project: project.trim(),
      path: path.trim(),
      ...(trimmedRef.length > 0 ? { ref: trimmedRef } : {}),
    })
    onClose()
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth fullScreen={fullScreen}>
      <DialogTitle>Embed GitLab file</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            label="Project"
            value={project}
            onChange={(e) => setProject(e.target.value)}
            required
            fullWidth
            helperText="Numeric id or namespaced path, e.g. propulsion/turbopump"
          />
          <TextField
            label="File path"
            value={path}
            onChange={(e) => setPath(e.target.value)}
            required
            fullWidth
            helperText="Path within the repository, e.g. docs/spec.md"
          />
          <TextField
            label="Ref (optional)"
            value={ref}
            onChange={(e) => setRef(e.target.value)}
            fullWidth
            helperText="Branch, tag, or commit — defaults to HEAD."
          />
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
