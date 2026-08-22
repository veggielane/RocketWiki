import { cacheExchange, createClient, fetchExchange } from 'urql'
import { getAccessToken } from './authToken'

const url = import.meta.env.VITE_GRAPHQL_URL ?? '/graphql'

/**
 * Single urql client for the app. `schema.graphql` doesn't exist yet (the
 * API hasn't been scaffolded), so nothing here has been exercised against
 * a live server — this is the wiring, not a tested integration.
 */
export const urqlClient = createClient({
  url,
  exchanges: [cacheExchange, fetchExchange],
  fetchOptions: () => {
    const token = getAccessToken()
    const headers: Record<string, string> = {}
    if (token) {
      headers.Authorization = `Bearer ${token}`
    }
    return { headers }
  },
})
