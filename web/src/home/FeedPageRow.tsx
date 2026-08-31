import type { ReactNode } from 'react'
import { Link as RouterLink } from 'react-router-dom'
import { Box, ListItem, ListItemButton, ListItemText, Stack, Typography } from '@mui/material'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { lookupPageIcon } from '../pages/pageIcons'
import { pageHref } from '../pages/pageSlug'
import type { FeedPageFragment } from '../graphql/generated/graphql'

export interface FeedPageRowProps {
  page: FeedPageFragment
  /** The feed's own fact about this page — who and when, how long since, when you last looked. */
  detail: ReactNode
}

/**
 * One page in a feed, rendered the same way in all three.
 *
 * Built from the search result's shape rather than a new one: a row is a link
 * to a page with its marking beside it, and the homepage inventing a second
 * idiom for that would be the drift a whole review round was spent removing.
 *
 * **The marking travels with the page.** These are cross-space reads, so a row
 * can name a page far more sensitive than anything else on screen — the badge
 * is what tells a reader that before they open it, exactly as it does in search
 * results (design.md §21). It is never colour alone: `MarkingLevelBadge` prints
 * the level's name.
 *
 * The space is shown by KEY, which is both what the cost budget affords (a
 * scalar, where `space { name }` is an object-field per row) and what search
 * and the page-list widget already do. It is also the only spelling that is
 * certainly right: the name would need a second lookup that can be missing.
 */
export function FeedPageRow({ page, detail }: FeedPageRowProps) {
  const Icon = lookupPageIcon(page.icon)?.Icon ?? ArticleOutlinedIcon

  return (
    // ListItem (an <li>) wraps the link — a bare <a> as a direct <ul> child is
    // invalid list markup (WCAG 1.3.1 / axe "list").
    <ListItem disablePadding>
      <ListItemButton component={RouterLink} to={pageHref(page.spaceKey, page.slug, page.id)} sx={{ py: 1 }}>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'flex-start', width: '100%', minWidth: 0 }}>
          {/* Decorative: the title beside it is the accessible name, and a
              second reading of "article" on every row is noise. */}
          <Icon fontSize="small" sx={{ color: 'text.secondary', mt: 0.25 }} aria-hidden />
          <ListItemText
            primary={page.title}
            secondary={detail}
            slotProps={{
              primary: { noWrap: true },
              // The detail line can hold an avatar and a badge, so it renders
              // as a div rather than the default <p>, to keep the markup valid.
              secondary: { component: 'div' },
            }}
            sx={{ my: 0, minWidth: 0 }}
          />
          <Box sx={{ flexShrink: 0, pt: 0.25 }}>
            <MarkingLevelBadge level={page.marking.level} levelName={page.marking.levelName} />
          </Box>
        </Stack>
      </ListItemButton>
    </ListItem>
  )
}

/** "in ENG", the space named the way every other cross-space list names it. */
export function InSpace({ spaceKey }: { spaceKey: string }) {
  return (
    <Typography component="span" variant="caption" color="text.secondary">
      in {spaceKey}
    </Typography>
  )
}
