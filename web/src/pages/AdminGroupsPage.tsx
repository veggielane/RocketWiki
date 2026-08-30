import { Alert, Stack, Typography } from '@mui/material'
import { type GridColDef } from '@mui/x-data-grid'
import { useKnownGroupsQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { RegistryDataGrid } from '../app/RegistryDataGrid'
import { useDocumentTitle } from '../app/documentTitle'

interface GroupRow {
  name: string
}

const columns: GridColDef<GroupRow>[] = [{ field: 'name', headerName: 'Group', flex: 1, minWidth: 240 }]

/**
 * The groups this instance has heard of.
 *
 * **Vocabulary, not authority, and the description says so out loud.** The wiki
 * does not own group membership — the identity provider does, and a group only
 * appears here once someone carrying it has signed in. So this screen can tell
 * an admin which names a rule may safely be written against, and can never tell
 * them who is in one; the rule builder's group field stays free-text for exactly
 * the same reason. An admin who reads this as "the list of groups" will
 * eventually wonder why a real group is missing, so the answer is on the page.
 *
 * A one-column `RegistryDataGrid` rather than a plain list: the other admin
 * registries are grids, this can grow long enough to want sorting, and one
 * screen in the set rendering its rows a different way is the drift the admin
 * sweep spent a whole round removing.
 */
export function AdminGroupsPage() {
  useDocumentTitle('Groups')
  const [{ data, fetching, error }] = useKnownGroupsQuery()
  const rows: GroupRow[] = (data?.groups ?? []).map((name) => ({ name }))

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Groups"
        description="Group names this instance has seen in a sign-in token. Membership lives in the identity provider and is never stored here, so this is the vocabulary access rules can be written against — not a directory, and never a list of who is in what. A group appears once someone carrying it signs in."
      />

      {/* Inline, with the heading still on screen — the shape the rest of the
          admin set settled on. */}
      {error && <Alert severity="info">{describeLoadFailure('GROUP_LIST').summary}</Alert>}

      {/* The sentence OR the table, never both. */}
      {!error && !fetching && rows.length === 0 && (
        <Typography color="text.secondary">
          No groups yet. Names appear here as people sign in — nobody carrying a group has signed in to this
          instance so far, so there is nothing to write a rule against yet.
        </Typography>
      )}

      {!error && (rows.length > 0 || fetching) && (
        <RegistryDataGrid
          aria-label="Known groups"
          rowCount={rows.length}
          rows={rows}
          columns={columns}
          getRowId={(row) => row.name}
          loading={fetching}
        />
      )}
    </Stack>
  )
}
