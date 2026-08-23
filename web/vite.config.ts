/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
//
// The dev server proxies every API path to the backend so the SPA's
// same-origin defaults (/graphql, /hubs, …) work in `vite dev` without the
// API needing a CORS policy it deliberately doesn't have. Target defaults to
// the API's standalone launch profile (http://localhost:5079); when the API
// runs under the Aspire AppHost its port is assigned dynamically — read it
// off the Aspire dashboard and set VITE_API_TARGET. See DEVELOPING.md.
const apiTarget = process.env.VITE_API_TARGET ?? 'http://localhost:5079'
const proxyPaths = ['/graphql', '/attachments', '/avatars', '/avatar', '/emojis', '/users']

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      ...Object.fromEntries(proxyPaths.map((p) => [p, { target: apiTarget, changeOrigin: true }])),
      '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
  },
})
