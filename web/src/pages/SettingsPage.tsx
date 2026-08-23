import { useState, type FormEvent } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  Paper,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import KeyOutlinedIcon from '@mui/icons-material/KeyOutlined'
import {
  useClearGitLabTokenMutation,
  useGitLabStatusQuery,
  useSetGitLabTokenMutation,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'

/**
 * User settings. v1 holds one section: the per-user GitLab personal access
 * token (design.md §18) — entered here, stored encrypted server-side, and
 * write-only by construction: nothing in the schema can return it, and this
 * page never displays it back (the field is cleared on submit; state is
 * only ever the boolean `viewerHasToken`).
 *
 * §15/§18 fail-closed extends to UI affordances: when the instance has no
 * GitLab configured the section is hidden entirely — the steady state on a
 * high-side replica, where mentioning the integration would only mislead.
 */
export function SettingsPage() {
  const [{ data, fetching }, refetchStatus] = useGitLabStatusQuery()
  const [{ fetching: saving }, setGitLabToken] = useSetGitLabTokenMutation()
  const [{ fetching: clearing }, clearGitLabToken] = useClearGitLabTokenMutation()
  const [tokenInput, setTokenInput] = useState('')
  const [feedback, setFeedback] = useState<{ severity: 'success' | 'warning'; message: string } | null>(null)

  const status = data?.gitlabStatus

  const handleSave = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const token = tokenInput.trim()
    if (token.length === 0) return
    // The field empties immediately, success or not — the token is
    // write-only and must never linger on screen.
    setTokenInput('')
    setFeedback(null)
    const result = await setGitLabToken({ input: { token } })
    const error = result.data?.setGitLabToken.error
    if (result.error !== undefined || error) {
      setFeedback({
        severity: 'warning',
        message: describeMutationError(error) ?? "Couldn't save the token.",
      })
      return
    }
    setFeedback({ severity: 'success', message: 'GitLab token saved.' })
    refetchStatus({ requestPolicy: 'network-only' })
  }

  const handleClear = async () => {
    setFeedback(null)
    const result = await clearGitLabToken({})
    const error = result.data?.clearGitLabToken.error
    if (result.error !== undefined || error) {
      setFeedback({
        severity: 'warning',
        message: describeMutationError(error) ?? "Couldn't clear the token.",
      })
      return
    }
    setFeedback({ severity: 'success', message: 'GitLab token cleared.' })
    refetchStatus({ requestPolicy: 'network-only' })
  }

  if (fetching) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="30%" height={48} />
        <Skeleton variant="rectangular" height={160} />
      </Stack>
    )
  }

  return (
    <Box>
      <Typography variant="h4" component="h1" sx={{ mb: 3 }}>
        Settings
      </Typography>

      {status?.configured !== true ? (
        <Typography color="text.secondary">There are no integration settings on this instance.</Typography>
      ) : (
        <Paper variant="outlined" sx={{ p: 3, maxWidth: 640 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
            <Typography variant="h6" component="h2">
              GitLab
            </Typography>
            {status.viewerHasToken ? (
              <Chip size="small" color="success" icon={<KeyOutlinedIcon />} label="Token saved" />
            ) : (
              <Chip size="small" icon={<KeyOutlinedIcon />} label="No token" />
            )}
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Live GitLab embeds on wiki pages ({status.baseUrl}) fetch with <em>your own</em> GitLab credential —
            there is no shared account. Create a personal access token in GitLab with the <code>read_api</code>{' '}
            scope and paste it here. It is stored encrypted, used only for your requests, and never shown again.
          </Typography>

          {feedback && (
            <Alert severity={feedback.severity} sx={{ mb: 2 }} onClose={() => setFeedback(null)}>
              {feedback.message}
            </Alert>
          )}

          <Box component="form" onSubmit={(e) => void handleSave(e)}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
              <TextField
                label="Personal access token"
                type="password"
                value={tokenInput}
                onChange={(e) => setTokenInput(e.target.value)}
                size="small"
                fullWidth
                autoComplete="off"
              />
              <Button type="submit" variant="contained" disabled={saving || tokenInput.trim().length === 0}>
                Save
              </Button>
            </Stack>
          </Box>

          {status.viewerHasToken && (
            <Button
              color="error"
              size="small"
              sx={{ mt: 2 }}
              onClick={() => void handleClear()}
              disabled={clearing}
            >
              Clear token
            </Button>
          )}
        </Paper>
      )}
    </Box>
  )
}
