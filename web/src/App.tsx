import { RouterProvider } from 'react-router-dom'
import { AuthProvider } from 'react-oidc-context'
import { Provider as UrqlProvider } from 'urql'
import { ColorModeProvider } from './theme/ColorModeProvider'
import { oidcConfig } from './auth/oidcConfig'
import { AuthTokenSync } from './graphql/AuthTokenSync'
import { urqlClient } from './graphql/client'
import { router } from './app/router'

export default function App() {
  return (
    <ColorModeProvider>
      <AuthProvider {...oidcConfig}>
        <AuthTokenSync />
        <UrqlProvider value={urqlClient}>
          <RouterProvider router={router} />
        </UrqlProvider>
      </AuthProvider>
    </ColorModeProvider>
  )
}
