import { useParams } from 'react-router-dom'
import { Alert, Stack, Typography } from '@mui/material'

/**
 * design.md §6.6: the permission inspector ("why can/can't user X see this
 * page") plus editing the page's own view/edit restrictions.
 *
 * NOTE (schema reconciliation): deliberately inert. The real schema
 * exposes NO read path for a page's restrictions (no `Page.restrictions`,
 * no per-page rule query) and no effective-permission inspector field —
 * only the blind `createAccessRule(kind: PAGE_RESTRICTION)` mutations.
 * Editing restrictions without being able to read the existing ones is a
 * footgun (an admin could stack a second restriction believing they were
 * replacing the first), so this page explains the gap instead of offering
 * the mutation. Reported as a contract gap — design.md §8's sketch
 * includes `restrictions: PageRestrictions!` and `effectivePermission`,
 * and §6.6 requires the inspector to ship with the feature. The
 * prop-driven inspector UI (access/permission/PermissionInspector.tsx) and
 * the rule builder remain intact and tested, ready to wire the day the
 * fields exist.
 */
export function PagePermissionsPage() {
  const { pageId } = useParams<{ pageId: string }>()

  if (!pageId) return null

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Page permissions
      </Typography>
      <Alert severity="info">
        The API doesn't yet expose a page's restrictions or the effective-permission inspector (design.md §6.6), so
        restrictions can't be viewed or edited from here. Space-level grants are managed from the space's Grants
        page.
      </Alert>
    </Stack>
  )
}
