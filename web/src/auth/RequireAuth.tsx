import { type ReactNode, useEffect } from 'react'
import { useAuth } from 'react-oidc-context'
import { Box, CircularProgress, Alert, Stack, Typography } from '@mui/material'

/**
 * Route guard: redirects to Keycloak when there is no signed-in user.
 * Cannot be exercised against a live Keycloak yet — see oidcConfig.ts.
 */
export function RequireAuth({ children }: { children: ReactNode }) {
  const auth = useAuth()

  useEffect(() => {
    if (!auth.isLoading && !auth.isAuthenticated && !auth.activeNavigator && !auth.error) {
      void auth.signinRedirect()
    }
  }, [auth])

  if (auth.error) {
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity="error">Sign-in failed: {auth.error.message}</Alert>
      </Box>
    )
  }

  if (!auth.isAuthenticated) {
    return (
      <Stack sx={{ height: '100vh', alignItems: 'center', justifyContent: 'center' }} spacing={2}>
        <CircularProgress />
        <Typography color="text.secondary">Redirecting to sign in…</Typography>
      </Stack>
    )
  }

  return <>{children}</>
}
