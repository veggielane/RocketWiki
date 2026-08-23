import { cacheExchange, createClient, fetchExchange } from 'urql'
import { graphqlTracingExchange } from '../telemetry/graphqlTracingExchange'
import { getAccessToken } from './authToken'

const url = import.meta.env.VITE_GRAPHQL_URL ?? '/graphql'

/**
 * Single urql client for the app. `schema.graphql` doesn't exist yet (the
 * API hasn't been scaffolded), so nothing here has been exercised against
 * a live server — this is the wiring, not a tested integration.
 */
export const urqlClient = createClient({
  url,
  // The tracing exchange sits *after* `cacheExchange` so it only sees
  // operations that actually reach the network — a cache hit is not a request
  // and should not produce a span. It is a no-op until a tracer provider is
  // registered, so it stays in the pipeline unconditionally.
  exchanges: [cacheExchange, graphqlTracingExchange(), fetchExchange],
  fetchOptions: () => {
    const token = getAccessToken()
    const headers: Record<string, string> = {}
    if (token) {
      headers.Authorization = `Bearer ${token}`
    }
    return { headers }
  },
})
