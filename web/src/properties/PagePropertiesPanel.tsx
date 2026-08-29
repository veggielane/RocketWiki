import { Fragment } from 'react'
import { Box, Paper, Typography } from '@mui/material'

export interface PagePropertyRow {
  keyId: string
  key: string
  value: string
}

export interface PagePropertiesPanelProps {
  /** Already ordered by the server (sortOrder, then key) — never re-sorted here. */
  properties: PagePropertyRow[]
}

/**
 * The page view's read-only properties panel (design.md §20): metadata shown
 * beside the page, never inside it.
 *
 * Read-only in the strict sense — there is no edit affordance here at all.
 * Managing properties moved to the page's details screen, and a panel that both
 * displayed and linked-to-editing was the thing that made every page carry a
 * chrome box whether or not it had any properties. A real `<dl>` rather than a styled grid
 * of `<div>`s — the key/value relationship is the whole content here, so it
 * has to survive being read by anything other than eyes. The two-column
 * layout is CSS grid on the `<dl>` itself, which keeps `<dt>`/`<dd>` as
 * direct children (no wrapper elements between them).
 */
export function PagePropertiesPanel({ properties }: PagePropertiesPanelProps) {
  if (properties.length === 0) return null

  return (
    <Paper variant="outlined" sx={{ p: 1.5, display: 'inline-block', minWidth: 240, maxWidth: '100%' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1 }}>
        <Typography variant="subtitle2" component="h2">
          Properties
        </Typography>
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
