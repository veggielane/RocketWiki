import type { ReactNode } from 'react'
import { Link, Stack, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'

export interface PageHeaderProps {
  /**
   * What this SCREEN is — "Details", "History", "Space settings". Not the
   * subject: see `subject`.
   */
  title: string
  /**
   * The thing the screen is about, and where to get back to it. Rendered as a
   * subtitle link under the heading rather than folded into it.
   *
   * Screens used to compose these as `History: {page.title}`, and the app ended
   * up with five spellings of the same idea — some with the subject in the h1,
   * some with it in a subtitle, and four of the nine with no way back to the
   * subject at all. One shape instead: the h1 names the screen (so it is stable,
   * which matters now that it also names the browser tab), and the subject gets
   * the line below, where it can be a link.
   *
   * Always the space/page NAME, never its key — `Analytics: PROP` and
   * `Trash: Propulsion` were the same space on adjacent screens.
   */
  subject?: { label: string; to: string }
  /** A plain sentence under the title, for screens whose subtitle is a scope note rather than a subject. */
  description?: ReactNode
  /**
   * A glyph beside the heading — the page view's own icon. Rendered outside the
   * `<h1>` so the heading's accessible name stays exactly the title, which is
   * also why callers pass decorative icons here rather than into `title`.
   */
  titleAdornment?: ReactNode
  /**
   * The screen's actions. Wrapped, not clipped: an un-wrapping row is how the
   * page view ended up with eight buttons squashing its own title (§7.3).
   */
  actions?: ReactNode
}

/**
 * The one page heading. Every screen's `<h1>` comes from here so the level, the
 * size and the back-link behaviour cannot drift apart again.
 */
export function PageHeader({ title, subject, description, titleAdornment, actions }: PageHeaderProps) {
  return (
    <Stack
      direction="row"
      spacing={2}
      useFlexGap
      sx={{ alignItems: 'flex-start', justifyContent: 'space-between', flexWrap: 'wrap' }}
    >
      <Stack spacing={0.5} sx={{ minWidth: 0 }}>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', minWidth: 0 }}>
          {titleAdornment}
          <Typography variant="h4" component="h1">
            {title}
          </Typography>
        </Stack>
        {subject && (
          /* MUI's Link, not a bare RouterLink: a bare one renders an unstyled
             <a> wearing the browser's default #0000ee, which fails contrast
             against the dark surface. The browser a11y layer caught it. */
          <Link component={RouterLink} to={subject.to} variant="body2">
            {subject.label}
          </Link>
        )}
        {description && (
          <Typography variant="body2" color="text.secondary">
            {description}
          </Typography>
        )}
      </Stack>
      {actions && (
        <Stack
          direction="row"
          spacing={1}
          useFlexGap
          sx={{ alignItems: 'center', flexWrap: 'wrap', justifyContent: 'flex-end' }}
        >
          {actions}
        </Stack>
      )}
    </Stack>
  )
}
