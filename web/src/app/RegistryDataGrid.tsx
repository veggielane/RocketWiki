import { Box } from '@mui/material'
import { DataGrid, type DataGridProps } from '@mui/x-data-grid'

/**
 * Row heights MUI X uses per density. Duplicated from the grid rather than
 * imported because there is no public export of them, and the alternative was
 * the magic `52 + 40 * rows.length` that three screens had each copied — with
 * `AdminPropertyKeysPage` passing `density="compact"` and still budgeting 40px
 * per row, so its grid was over-tall by 4px per row and grew wronger with the
 * registry.
 */
const ROW_HEIGHT = { compact: 36, standard: 52, comfortable: 67 } as const
const HEADER_HEIGHT = 56

export interface RegistryDataGridProps extends DataGridProps {
  /** How many rows to size for. The grid's own `rows` may be empty while loading. */
  rowCount: number
}

/**
 * A short, footer-less grid sized to its contents — the shape every admin
 * registry wants (emoji, property keys, sync). The audit log is deliberately
 * NOT one of these: it is long, paged, and fills the height it is given, so it
 * uses `DataGrid` directly.
 */
export function RegistryDataGrid({ rowCount, density = 'compact', ...rest }: RegistryDataGridProps) {
  const height = HEADER_HEIGHT + ROW_HEIGHT[density] * Math.max(rowCount, 1)
  return (
    <Box sx={{ height }}>
      <DataGrid density={density} hideFooter disableRowSelectionOnClick {...rest} />
    </Box>
  )
}
