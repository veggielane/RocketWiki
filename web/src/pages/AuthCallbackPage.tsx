import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from 'react-oidc-context'
import { CircularProgress, Stack, Typography } from '@mui/material'

/** Keycloak redirects here after login; react-oidc-context processes the code exchange. */
export function AuthCallbackPage() {
  const auth = useAuth()
  const navigate = useNavigate()

  useEffect(() => {
    if (auth.isAuthenticated) {
      navigate('/', { replace: true })
    }
  }, [auth.isAuthenticated, navigate])

  return (
    <Stack spacing={2} sx={{ height: '100vh', alignItems: 'center', justifyContent: 'center' }}>
      <CircularProgress />
      <Typography color="text.secondary">Completing sign-in…</Typography>
    </Stack>
  )
}
