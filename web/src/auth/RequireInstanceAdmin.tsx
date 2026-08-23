import type { ReactNode } from 'react'
import { AccessGate } from './AccessGate'
import { useIsInstanceAdmin } from './useIsInstanceAdmin'

/**
 * Gates instance-wide admin surfaces (the admin index, the audit log
 * viewer, sync status) on the realm "admin" role from the ID token — see
 * useIsInstanceAdmin.ts for why this reads a claim rather than a
 * server-computed flag (the real CurrentUser type doesn't carry one;
 * reported as a contract gap). This is UX that prevents dead ends, not the
 * security boundary: every query behind it is admin-enforced server-side.
 */
export function RequireInstanceAdmin({ children }: { children: ReactNode }) {
  const { isInstanceAdmin, loading } = useIsInstanceAdmin()

  return (
    <AccessGate allowed={isInstanceAdmin} loading={loading}>
      {children}
    </AccessGate>
  )
}
