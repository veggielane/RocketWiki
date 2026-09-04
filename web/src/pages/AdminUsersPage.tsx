import { Alert, Button, Chip, Stack, Typography } from '@mui/material'
import { type GridColDef } from '@mui/x-data-grid'
import { useSearchParams } from 'react-router-dom'
import { useAdminUsersQuery, type AdminUsersQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { RegistryDataGrid } from '../app/RegistryDataGrid'
import { useDocumentTitle } from '../app/documentTitle'
import { UserAvatar } from '../avatars/UserAvatar'
import { formatTimestamp } from '../format/dateTime'
import { MAX_PAGE_SIZE, PAGE_SIZE, shownFromParams } from './adminUsersPaging'

type UserRow = NonNullable<NonNullable<AdminUsersQuery['users']>['nodes']>[number]

/**
 * The account roster (instance admins only, gated at the router and enforced
 * by the server, which also audits the read as `admin.users.view`).
 *
 * **Identity and activity, and nothing else.** There is no nationality or
 * group column because there is no such field to select:
 * those are token claims evaluated per request, never stored per user. A
 * roster that listed them would be a second, stale answer to "who is allowed
 * what" — and the page says so, because an admin who expects to find
 * permissions here should be told where they actually live.
 */
export function AdminUsersPage() {
  useDocumentTitle('Users')
  const [params, setParams] = useSearchParams()
  const shown = shownFromParams(params.get('show'))

  const [{ data, fetching, error }] = useAdminUsersQuery({ variables: { first: shown } })
  const rows: UserRow[] = data?.users?.nodes ?? []
  const totalCount = data?.users?.totalCount
  const hasMore = data?.users?.pageInfo.hasNextPage === true

  const showMore = () =>
    setParams((current) => {
      const next = new URLSearchParams(current)
      next.set('show', String(Math.min(shown + PAGE_SIZE, MAX_PAGE_SIZE)))
      return next
    })

  const columns: GridColDef<UserRow>[] = (
    [
      {
        field: 'displayName',
        headerName: 'User',
        flex: 1,
        minWidth: 220,
        renderCell: (params) => (
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', height: '100%' }}>
            <UserAvatar
              userId={params.row.id}
              hasAvatar={params.row.hasAvatar}
              displayName={params.row.displayName}
              size={24}
            />
            <span>{params.row.displayName}</span>
          </Stack>
        ),
      },
      { field: 'email', headerName: 'Email', flex: 1, minWidth: 200, valueGetter: (value: string | null) => value ?? '' },
      {
        field: 'isExternal',
        headerName: 'Account',
        width: 120,
        // A chip only for the exceptional case. Badging every ordinary account
        // "Internal" would put a label on every row that carries no signal, and
        // leave the one that matters no louder than the rest.
        renderCell: (params) => (params.row.isExternal ? <Chip size="small" label="External" variant="outlined" /> : null),
      },
      {
        field: 'lastSeenAtUtc',
        headerName: 'Last seen',
        width: 190,
        // The viewer's own locale and timezone, via the shared formatter — the
        // convention every admin table but the audit log follows. The audit log
        // is raw UTC on purpose, because it is read across timezones and must
        // not shift under the reader; a roster has no such contract.
        valueFormatter: (value: string) => formatTimestamp(value),
      },
      {
        field: 'createdAtUtc',
        headerName: 'First seen',
        width: 190,
        valueFormatter: (value: string) => formatTimestamp(value),
      },
    ] satisfies GridColDef<UserRow>[]
  ).map((column) => ({ ...column, sortable: false }))
  // Not sortable, for the reason the audit log's columns are not: `rows` is the
  // slice fetched so far, not the roster. A header click would reorder those
  // rows and present the result as an answer about everyone. The server orders
  // by display name (then id, so paging is stable), which the description says.

  return (
    <Stack spacing={2}>
      <PageHeader
        title="Users"
        description="Every account this instance has seen, in alphabetical order. Identity and activity only — group membership and nationality arrive in a sign-in token and are never stored against an account, so they cannot be listed here; access is decided per request from the token, and rules are written on a space's Grants screen."
      />

      {/* Inline, heading retained. A failed read is not an empty roster, and
          the roster is exactly the list where "no users" would be alarming
          rather than merely wrong. The route already gates non-admins
          (RequireInstanceAdmin), and the server refuses them regardless — this
          covers the case where those two disagree, or the API is simply down. */}
      {error && <Alert severity="info">{describeLoadFailure('USER_ROSTER').summary}</Alert>}

      {!error && totalCount !== undefined && (
        <Typography variant="body2" color="text.secondary">
          {/* An EXACT total, unlike search's. Nothing in this list is filtered
              per row, so counting it reveals nothing a row would not. */}
          {totalCount.toLocaleString()} account{totalCount === 1 ? '' : 's'}
          {rows.length < totalCount ? ` — showing ${rows.length.toLocaleString()}` : ''}
        </Typography>
      )}

      {!error && !fetching && rows.length === 0 && (
        <Typography color="text.secondary">
          No accounts yet. A row appears the first time someone signs in — this instance has had no sign-ins.
        </Typography>
      )}

      {!error && (rows.length > 0 || fetching) && (
        <RegistryDataGrid
          aria-label="User accounts"
          rowCount={rows.length}
          rows={rows}
          columns={columns}
          getRowId={(row) => row.id}
          loading={fetching}
        />
      )}

      {!error && hasMore && shown < MAX_PAGE_SIZE && (
        <Button variant="outlined" size="small" disabled={fetching} onClick={showMore} sx={{ alignSelf: 'flex-start' }}>
          {fetching ? 'Loading…' : `Show ${PAGE_SIZE} more`}
        </Button>
      )}

      {/* Said out loud rather than left as a button that stops working. The
          roster can be longer than one request may ask for, and walking further
          needs cursor paging this screen does not do — so the honest thing is to
          name the limit and the ordering, not to imply the list ends here. */}
      {!error && hasMore && shown >= MAX_PAGE_SIZE && (
        <Typography variant="body2" color="text.secondary">
          This is as far as the list goes for now — {MAX_PAGE_SIZE} accounts is the most one request may ask for.
          The order is alphabetical, so the rest continue from where this leaves off.
        </Typography>
      )}
    </Stack>
  )
}
