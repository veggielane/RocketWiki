import { Link as RouterLink } from 'react-router-dom'
import { Alert, Card, CardActionArea, CardContent, Chip, Stack, Typography } from '@mui/material'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'

/**
 * The design.md section numbers these descriptions used to carry ("(§7)",
 * "(design.md §6.5)") are gone. design.md is not shipped to the instance and is
 * not linked from anywhere in the app, so to the admin reading this they were
 * noise pointing at a document they cannot open. The facts they were compressing
 * are now written out.
 */
const LIVE_SECTIONS = [
  {
    title: 'Analytics',
    description: 'What is being read, edited and searched for, site-wide.',
    to: '/admin/analytics',
  },
  {
    title: 'Audit log',
    description: 'Filter by user, action, subject, outcome and date; export the result as CSV.',
    to: '/admin/audit',
  },
  {
    title: 'Sync status',
    description: 'Per-space outbox positions and pending events; per-origin bundle import history.',
    to: '/admin/sync',
  },
  {
    title: 'Users',
    description: 'Every account this instance has seen — names, email and activity. No permissions data.',
    to: '/admin/users',
  },
  {
    title: 'Groups',
    description:
      'The group names this instance has seen in a sign-in token — the vocabulary access rules can name.',
    to: '/admin/groups',
  },
  {
    title: 'Custom emojis',
    description: 'Curate the :name: registry every signed-in user can render.',
    to: '/admin/emojis',
  },
  {
    title: 'Page property keys',
    description: "Define the key vocabulary editors pick from on a page's properties screen.",
    to: '/admin/property-keys',
  },
]

/**
 * Not built yet. Rendered visibly differently from the live cards above rather
 * than as identical-looking cards that simply do not respond to a click — the
 * only thing distinguishing them was the absence of a `CardActionArea`, which is
 * invisible until you press one.
 */
const PLANNED_SECTIONS = [
  {
    title: 'Spaces',
    description:
      'Create spaces and manage exports. Grants are per-space — open a space and use its Grants screen.',
  },
  { title: 'Attribute registry', description: 'Declare which token claims are rule-usable attributes.' },
]

/**
 * Instance-admin-only (gated by `RequireInstanceAdmin` at the router
 * level — see app/router.tsx). Rule builder and permission inspector
 * aren't listed here even as placeholders: they're space/page-scoped, not
 * instance-wide, reached from a space's Grants page or a page's
 * Permissions button instead.
 */
export function AdminPage() {
  useDocumentTitle('Admin')

  return (
    <Stack spacing={2}>
      <PageHeader title="Admin" />
      <Alert severity="info">
        Every screen here is server-enforced, and instance admins deliberately do not bypass page restrictions.
      </Alert>
      {LIVE_SECTIONS.map((section) => (
        <Card key={section.title} variant="outlined">
          <CardActionArea component={RouterLink} to={section.to}>
            <CardContent>
              <Typography variant="h6" component="h2">
                {section.title}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {section.description}
              </Typography>
            </CardContent>
          </CardActionArea>
        </Card>
      ))}

      <Typography variant="h6" component="h2" sx={{ pt: 2 }}>
        Not built yet
      </Typography>
      {PLANNED_SECTIONS.map((section) => (
        <Card key={section.title} variant="outlined" sx={{ borderStyle: 'dashed' }}>
          <CardContent>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
              <Typography variant="h6" component="h3">
                {section.title}
              </Typography>
              {/* The label, not just the dashed border: a border is a colour-free
                  cue but still a purely visual one, and "why does this card do
                  nothing" deserves an answer in words. */}
              <Chip size="small" label="Planned" />
            </Stack>
            <Typography variant="body2" color="text.secondary">
              {section.description}
            </Typography>
          </CardContent>
        </Card>
      ))}
    </Stack>
  )
}
