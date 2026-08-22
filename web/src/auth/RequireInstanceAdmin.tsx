import type { ReactNode } from 'react'
import { useCurrentUserQuery } from '../graphql/generated/graphql'
import { AccessGate } from './AccessGate'

/**
 * Gates instance-wide admin surfaces (the admin index, the audit log
 * viewer) on `CurrentUser.isInstanceAdmin` — a server-computed flag, not a
 * client-side JWT-claim guess, matching how `page.canEdit` already works
 * elsewhere in this app. Space-scoped admin surfaces (space grants, a
 * page's permissions) instead check that resource's own
 * `canManageAccess` field, which is a superset of this (instance admin OR
 * that space's admin) — see PagePermissionsPage.tsx / SpaceGrantsPage.tsx.
 */
export function RequireInstanceAdmin({ children }: { children: ReactNode }) {
  const [{ data, fetching, error }] = useCurrentUserQuery()

  return (
    <AccessGate allowed={!error && data?.me.isInstanceAdmin} loading={fetching}>
      {children}
    </AccessGate>
  )
}
