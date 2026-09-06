import { Link as RouterLink } from 'react-router-dom'
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  Box,
  Button,
  List,
  ListItem,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Paper,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import HubOutlinedIcon from '@mui/icons-material/HubOutlined'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import { usePageLinksForPageQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { lookupPageIcon } from '../pages/pageIcons'
import { pageHref } from '../pages/pageSlug'
import { graphPath, type GraphNode } from './graphModel'

const pages = (count: number): string => `${count} ${count === 1 ? 'page' : 'pages'}`

interface LinkGroupProps {
  id: string
  /** The count phrase — "Links to 3 pages". The count is the server's, and it is the list's length by construction. */
  heading: string
  linked: readonly GraphNode[]
  /** What to say instead of an empty, expandable nothing. */
  empty: string
}

function LinkGroup({ id, heading, linked, empty }: LinkGroupProps) {
  if (linked.length === 0) {
    return (
      <Paper variant="outlined" sx={{ px: 2, py: 1.5 }}>
        <Typography variant="subtitle2" component="h3">
          {heading}
        </Typography>
        <Typography variant="body2" color="text.secondary">
          {empty}
        </Typography>
      </Paper>
    )
  }
  return (
    // MUI wraps the summary in its own <h3> (`slotProps.heading`), so the
    // label inside is a span rather than a second heading.
    <Accordion variant="outlined" disableGutters slotProps={{ heading: { component: 'h3' } }}>
      <AccordionSummary expandIcon={<ExpandMoreIcon />} aria-controls={`${id}-links`} id={`${id}-summary`}>
        <Typography variant="subtitle2" component="span">
          {heading}
        </Typography>
      </AccordionSummary>
      <AccordionDetails id={`${id}-links`} sx={{ pt: 0 }}>
        <List dense disablePadding aria-labelledby={`${id}-summary`}>
          {linked.map((page) => {
            const Icon = lookupPageIcon(page.icon)?.Icon ?? ArticleOutlinedIcon
            return (
              // A real <li> around each link: a ListItemButton rendered as an
              // anchor straight inside the <ul> is the `ul > a` shape axe
              // rejects (docs/ACCESSIBILITY.md has fixed it three times).
              <ListItem key={page.id} disablePadding>
                <ListItemButton
                  component={RouterLink}
                  to={pageHref(page.spaceKey, page.slug, page.id)}
                  sx={{ borderRadius: 1 }}
                >
                  <ListItemIcon sx={{ minWidth: 32 }}>
                    <Icon fontSize="small" aria-hidden />
                  </ListItemIcon>
                  <ListItemText primary={page.title} secondary={page.spaceKey} />
                  <MarkingLevelBadge level={page.marking.level} levelName={page.marking.levelName} />
                </ListItemButton>
              </ListItem>
            )
          })}
        </List>
      </AccordionDetails>
    </Accordion>
  )
}

/**
 * A page's place in the document graph, on the details screen: how many
 * pages it links to and how many link to it, each opening into the list
 * behind the number (design.md §6.7 / §21.8).
 *
 * Both lists hold only pages the caller can view, and each count is its
 * list's length — computed once, server-side — so a count here can never
 * say "one more" than the list shows and imply a page that was withheld. The
 * section says "you can view" because that is the honest reading of the
 * numbers, exactly as the analytics screen does.
 */
export function PageLinksSection({ pageId }: { pageId: string }) {
  const [{ data, fetching, error }] = usePageLinksForPageQuery({ variables: { id: pageId } })

  if (fetching && !data) {
    return <Skeleton variant="rectangular" height={96} />
  }
  if (error || !data?.page) {
    return <Alert severity="info">{describeLoadFailure('PAGE_LINKS').summary}</Alert>
  }
  const page = data.page

  return (
    <Stack spacing={1.5}>
      <LinkGroup
        id="outbound"
        heading={`Links to ${pages(page.outboundLinkCount)}`}
        linked={page.outboundLinks}
        empty="This page links to no other page you can view."
      />
      <LinkGroup
        id="inbound"
        heading={`Linked from ${pages(page.inboundLinkCount)}`}
        linked={page.inboundLinks}
        empty="No page you can view links here."
      />
      <Box>
        <Button
          component={RouterLink}
          to={graphPath({ focus: pageId })}
          startIcon={<HubOutlinedIcon />}
          variant="outlined"
          size="small"
        >
          View on graph
        </Button>
      </Box>
    </Stack>
  )
}
