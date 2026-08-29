import { Link as RouterLink, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Divider,
  Link,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Paper,
  Stack,
  Typography,
} from '@mui/material'
import { RichTextEditor } from '../editor/RichTextEditor'
import { HELP_SECTIONS, HELP_TOPICS, findHelpTopic } from '../help/topics'

/**
 * In-app help, at `/-/docs`.
 *
 * Under the reserved `-` segment for the same reason a space's own screens are:
 * it is a system page, and putting it there means no future top-level route can
 * be shadowed by it or shadow it.
 *
 * Not gated. Help has to work for the reader most likely to need it — someone
 * new, who may hold a role in nothing yet — and there is nothing here to protect:
 * these topics describe how grants, markings and audit WORK, not who holds what.
 *
 * Rendered through the same read-only `RichTextEditor` the page view uses, so a
 * help topic gets the same headings, tables and code blocks a wiki page does. A
 * second Markdown pipeline is exactly what design.md §4 forbids, and help is not
 * special enough to earn one.
 */
export function HelpPage() {
  const { topic: slug } = useParams<{ topic?: string }>()
  const topic = findHelpTopic(slug ?? HELP_TOPICS[0]?.slug)

  return (
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} alignItems="flex-start">
      <Paper variant="outlined" sx={{ width: { xs: '100%', md: 280 }, flexShrink: 0 }}>
        <Box sx={{ p: 2, pb: 1 }}>
          <Typography variant="h6" component="h1">
            Help
          </Typography>
        </Box>
        {HELP_SECTIONS.map((section, index) => (
          <Box key={section.title}>
            {index > 0 && <Divider />}
            <Typography
              variant="overline"
              component="h2"
              color="text.secondary"
              sx={{ display: 'block', px: 2, pt: 1.5 }}
            >
              {section.title}
            </Typography>
            {/* <li> wrapping each link: MuiList renders a <ul>, and a link as a
                direct child of one is an axe "list" violation — the same one the
                space nav and the settings screen have each shipped once. */}
            <List dense>
              {section.topics.map((t) => (
                <ListItem key={t.slug} disablePadding>
                  <ListItemButton
                    component={RouterLink}
                    to={`/-/docs/${t.slug}`}
                    selected={t.slug === topic?.slug}
                  >
                    <ListItemText primary={t.title} secondary={t.summary} />
                  </ListItemButton>
                </ListItem>
              ))}
            </List>
          </Box>
        ))}
      </Paper>

      <Box sx={{ flexGrow: 1, minWidth: 0 }}>
        {topic ? (
          <Paper variant="outlined" sx={{ p: { xs: 2, md: 3 } }}>
            <RichTextEditor
              initialMarkdown={topic.markdown}
              editable={false}
              showToolbar={false}
              ariaLabel={`Help: ${topic.title}`}
            />
          </Paper>
        ) : (
          <Alert severity="info">
            No help topic called "{slug}".{' '}
            <Link component={RouterLink} to="/-/docs">
              Back to help
            </Link>
            .
          </Alert>
        )}
      </Box>
    </Stack>
  )
}
