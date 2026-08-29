import { Link as RouterLink } from 'react-router-dom'
import { Alert, Card, CardActionArea, CardContent, Stack, Typography } from '@mui/material'

const LIVE_SECTIONS = [
  {
    title: 'Analytics',
    description: 'What is being read, edited and searched for, site-wide (§7).',
    to: '/admin/analytics',
  },
  {
    title: 'Audit log',
    description: 'Filter by user/action/subject/outcome/date, CSV export (§7).',
    to: '/admin/audit',
  },
  {
    title: 'Sync status',
    description: 'Per-space outbox positions and pending events; per-origin bundle import history (§12).',
    to: '/admin/sync',
  },
  {
    title: 'Custom emojis',
    description: 'Curate the :name: registry every signed-in user can render (§19).',
    to: '/admin/emojis',
  },
  {
    title: 'Page property keys',
    description: "Define the key vocabulary editors pick from on a page's properties screen (§20).",
    to: '/admin/property-keys',
  },
]

const PLACEHOLDER_SECTIONS = [
  {
    title: 'Spaces',
    description:
      'Create spaces and manage exports (§12). Grants live per-space — open a space and use its "Grants" action.',
  },
  { title: 'Attribute registry', description: 'Declare which token claims are rule-usable attributes (§6.2).' },
]

/**
 * Instance-admin-only (gated by `RequireInstanceAdmin` at the router
 * level — see app/router.tsx). Rule builder and permission inspector
 * aren't listed here even as placeholders: they're space/page-scoped, not
 * instance-wide, reached from a space's Grants page or a page's
 * Permissions button instead.
 */
export function AdminPage() {
  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Admin
      </Typography>
      <Alert severity="info">
        Every real screen here is server-enforced — instance admins deliberately do not bypass page restrictions
        (design.md §6.5).
      </Alert>
      {LIVE_SECTIONS.map((section) => (
        <Card key={section.title} variant="outlined">
          <CardActionArea component={RouterLink} to={section.to}>
            <CardContent>
              <Typography variant="h6">{section.title}</Typography>
              <Typography variant="body2" color="text.secondary">
                {section.description}
              </Typography>
            </CardContent>
          </CardActionArea>
        </Card>
      ))}
      {PLACEHOLDER_SECTIONS.map((section) => (
        <Card key={section.title} variant="outlined">
          <CardContent>
            <Typography variant="h6">{section.title}</Typography>
            <Typography variant="body2" color="text.secondary">
              {section.description}
            </Typography>
          </CardContent>
        </Card>
      ))}
    </Stack>
  )
}
