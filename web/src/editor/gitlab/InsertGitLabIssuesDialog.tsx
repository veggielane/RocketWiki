import { useState } from 'react'
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Stack, TextField } from '@mui/material'
import type { GitLabIssuesSpec } from '../../gitlab/fenceBody'
import { useDialogFullScreen } from '../../app/useDialogFullScreen'

export interface InsertGitLabIssuesDialogProps {
  open: boolean
  onClose: () => void
  onInsert: (spec: GitLabIssuesSpec) => void
}

/**
 * Insert affordance for the ` ```gitlab-issues ` fence (design.md §18): the
 * fields mirror GitLabIssueFilterInput; only non-empty ones become
 * key=value lines. The select options are conveniences over GitLab's own
 * vocabulary — anything out of vocabulary would simply degrade to unset
 * server-side, the client never validates the server's words.
 */
export function InsertGitLabIssuesDialog({ open, onClose, onInsert }: InsertGitLabIssuesDialogProps) {
  const fullScreen = useDialogFullScreen()
  const [project, setProject] = useState('')
  const [state, setState] = useState('')
  const [labels, setLabels] = useState('')
  const [search, setSearch] = useState('')
  const [milestone, setMilestone] = useState('')
  const [orderBy, setOrderBy] = useState('')
  const [sort, setSort] = useState('')
  const [first, setFirst] = useState('')
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) {
      setProject('')
      setState('')
      setLabels('')
      setSearch('')
      setMilestone('')
      setOrderBy('')
      setSort('')
      setFirst('')
    }
  }

  const firstValid = first.length === 0 || /^\d+$/.test(first)
  const canInsert = project.trim().length > 0 && firstValid

  const handleInsert = () => {
    if (!canInsert) return
    const labelList = labels
      .split(',')
      .map((label) => label.trim())
      .filter((label) => label.length > 0)
    const spec: GitLabIssuesSpec = { project: project.trim() }
    if (state) spec.state = state
    if (labelList.length > 0) spec.labels = labelList
    if (search.trim()) spec.search = search.trim()
    if (milestone.trim()) spec.milestone = milestone.trim()
    if (orderBy) spec.orderBy = orderBy
    if (sort) spec.sort = sort
    if (first.length > 0) spec.first = Number(first)
    onInsert(spec)
    onClose()
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth fullScreen={fullScreen}>
      <DialogTitle>Insert GitLab issue list</DialogTitle>
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
          <TextField label="State" select value={state} onChange={(e) => setState(e.target.value)} fullWidth>
            <MenuItem value="">Any</MenuItem>
            <MenuItem value="opened">Open</MenuItem>
            <MenuItem value="closed">Closed</MenuItem>
          </TextField>
          <TextField
            label="Labels"
            value={labels}
            onChange={(e) => setLabels(e.target.value)}
            fullWidth
            helperText="Comma-separated, e.g. bug,priority::high"
          />
          <TextField label="Search" value={search} onChange={(e) => setSearch(e.target.value)} fullWidth />
          <TextField label="Milestone" value={milestone} onChange={(e) => setMilestone(e.target.value)} fullWidth />
          <TextField label="Order by" select value={orderBy} onChange={(e) => setOrderBy(e.target.value)} fullWidth>
            <MenuItem value="">Default</MenuItem>
            <MenuItem value="created_at">Created</MenuItem>
            <MenuItem value="updated_at">Updated</MenuItem>
          </TextField>
          <TextField label="Sort" select value={sort} onChange={(e) => setSort(e.target.value)} fullWidth>
            <MenuItem value="">Default</MenuItem>
            <MenuItem value="desc">Newest first</MenuItem>
            <MenuItem value="asc">Oldest first</MenuItem>
          </TextField>
          <TextField
            label="Max results"
            value={first}
            onChange={(e) => setFirst(e.target.value)}
            fullWidth
            error={!firstValid}
            helperText={firstValid ? 'Defaults to 20; the server caps at 50.' : 'Digits only.'}
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
