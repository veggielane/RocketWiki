import { Alert, Box, Stack, Typography } from '@mui/material'

/**
 * What a build with no realm shows instead of a sign-in redirect.
 *
 * The alternative — the old `http://localhost:8080/realms/rocketwiki` default —
 * looked like a working app right up until someone pressed sign in and got sent
 * to a host on their own machine. A screen that names the missing setting turns
 * a mis-built image into a five-second diagnosis instead of a support ticket
 * about a broken login.
 *
 * Deliberately plain and dependency-light: it renders before any router, any
 * API client, and any authenticated data, because none of those can work yet.
 */
export function AuthNotConfigured() {
  return (
    <Box component="main" sx={{ p: 4, maxWidth: 720, mx: 'auto' }}>
      <Stack spacing={2}>
        <Typography variant="h1" sx={{ fontSize: '1.75rem' }}>
          Sign-in is not configured
        </Typography>
        <Alert severity="warning">
          This build was made without an identity provider, so there is nothing to sign in to.
        </Alert>
        <Typography variant="body1">
          The address of the Keycloak realm is baked in when the web app is built, not read at run time. Rebuild the
          image with <code>VITE_OIDC_AUTHORITY</code> set to your realm — for example{' '}
          <code>https://keycloak.example.internal/realms/rocketwiki</code> — or set it in <code>web/.env.local</code>{' '}
          when running the dev server directly.
        </Typography>
        <Typography variant="body2" color="text.secondary">
          There is no default on purpose. A guessed address would point every sign-in at a machine nobody chose.
        </Typography>
      </Stack>
    </Box>
  )
}
