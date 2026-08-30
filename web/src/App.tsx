import { RouterProvider } from 'react-router-dom'
import { AuthProvider } from 'react-oidc-context'
import { Provider as UrqlProvider } from 'urql'
import { ColorModeProvider } from './theme/ColorModeProvider'
import { oidcConfig } from './auth/oidcConfig'
import { AuthNotConfigured } from './auth/AuthNotConfigured'
import { AuthTokenSync } from './graphql/AuthTokenSync'
import { urqlClient } from './graphql/client'
import { router } from './app/router'

export default function App() {
  return (
    <ColorModeProvider>
      {/* No realm, no app. Mounting the router behind an AuthProvider pointed at
          a guessed authority is what made a mis-built image look healthy until
          the first sign-in; this says so on the first screen instead. */}
      {oidcConfig === null ? (
        <AuthNotConfigured />
      ) : (
        <AuthProvider {...oidcConfig}>
          <AuthTokenSync />
          <UrqlProvider value={urqlClient}>
            <RouterProvider router={router} />
          </UrqlProvider>
        </AuthProvider>
      )}
    </ColorModeProvider>
  )
}
