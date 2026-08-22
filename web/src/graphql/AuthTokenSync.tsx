import { useEffect } from 'react'
import { useAuth } from 'react-oidc-context'
import { setAccessToken } from './authToken'

/** Mount once near the app root, inside AuthProvider. Renders nothing. */
export function AuthTokenSync() {
  const auth = useAuth()

  useEffect(() => {
    setAccessToken(auth.user?.access_token)
    return () => setAccessToken(undefined)
  }, [auth.user?.access_token])

  return null
}
