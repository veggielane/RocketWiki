import type { ReactNode } from 'react'
import { Skeleton, Stack } from '@mui/material'
import { NotFoundPage } from '../pages/NotFoundPage'

export interface AccessGateProps {
  /** `undefined` while unknown (still loading, or the query errored). */
  allowed: boolean | undefined
  loading: boolean
  children: ReactNode
}

/**
 * Presentational gate: this is UX that prevents dead ends, not the security
 * boundary (the server is — see design.md §6.7 and NotFoundPage.tsx's
 * comment). Fails closed: any state that isn't a confirmed `true` (still
 * loading aside) renders as not-found, including "the query errored," which
 * matters most when there's no live API to answer at all — this scaffold
 * has no backend running, so every gate below denies until one exists. That
 * is the correct behavior, not a bug to route around with a dev bypass.
 */
export function AccessGate({ allowed, loading, children }: AccessGateProps) {
  if (loading) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={200} />
      </Stack>
    )
  }

  if (!allowed) {
    return <NotFoundPage />
  }

  return <>{children}</>
}
