import { Fragment } from 'react'
import { Link as RouterLink } from 'react-router-dom'
import { Box, Button, Paper, Typography } from '@mui/material'
import TuneOutlinedIcon from '@mui/icons-material/TuneOutlined'

export interface PagePropertyRow {
  keyId: string
  key: string
  value: string
}

export interface PagePropertiesPanelProps {
  /** Already ordered by the server (sortOrder, then key) — never re-sorted here. */
  properties: PagePropertyRow[]
  /**
   * Route to the properties screen. Passed only when the viewer holds
   * canEdit, so a read-only viewer is never offered an edit affordance the
   * server would refuse (design.md §6, §20.2).
   */
  editHref?: string
}

/**
 * The page view's read-only properties panel (design.md §20): metadata shown
 * beside the page, never inside it. A real `<dl>` rather than a styled grid
 * of `<div>`s — the key/value relationship is the whole content here, so it
 * has to survive being read by anything other than eyes. The two-column
 * layout is CSS grid on the `<dl>` itself, which keeps `<dt>`/`<dd>` as
 * direct children (no wrapper elements between them).
 */
export function PagePropertiesPanel({ properties, editHref }: PagePropertiesPanelProps) {
  if (properties.length === 0 && !editHref) return null

  return (
    <Paper variant="outlined" sx={{ p: 1.5, display: 'inline-block', minWidth: 240, maxWidth: '100%' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: properties.length > 0 ? 1 : 0 }}>
        <Typography variant="subtitle2" component="h2">
          Properties
        </Typography>
        {editHref && (
          <Button component={RouterLink} to={editHref} size="small" startIcon={<TuneOutlinedIcon />}>
            {properties.length > 0 ? 'Edit properties' : 'Add properties'}
          </Button>
        )}
      </Box>
      {properties.length > 0 && (
        <Box
          component="dl"
          sx={{
            display: 'grid',
            gridTemplateColumns: 'max-content minmax(0, 1fr)',
            columnGap: 2,
            rowGap: 0.5,
            m: 0,
          }}
        >
          {properties.map((property) => (
            <Fragment key={property.keyId}>
              <Typography component="dt" variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
                {property.key}
              </Typography>
              <Typography component="dd" variant="body2" sx={{ m: 0, overflowWrap: 'anywhere' }}>
                {property.value}
              </Typography>
            </Fragment>
          ))}
        </Box>
      )}
    </Paper>
  )
}
