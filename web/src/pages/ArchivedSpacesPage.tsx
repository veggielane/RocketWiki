import { Alert, Button, List, ListItem, ListItemText, Skeleton, Stack, Typography } from '@mui/material'
import { useArchivedSpacesQuery, useRestoreSpaceMutation } from '../graphql/generated/graphql'

/**
 * design.md §6.5.1: restore requires instance admin OR that space's own
 * space-admin. Not client-gated here — the query itself is
 * permission-filtered server-side (a space-admin sees only their own
 * archived spaces, an instance admin sees all), same "absent rather than
 * forbidden" pattern used for trash: someone with nothing to restore just
 * sees an empty list, not a distinguishable "no access" screen.
 */
export function ArchivedSpacesPage() {
  const [{ data, fetching, error }, refetch] = useArchivedSpacesQuery()
  const [, restoreSpace] = useRestoreSpaceMutation()

  if (fetching) {
    return <Skeleton variant="rectangular" height={200} />
  }

  if (error || !data) {
    return <Alert severity="info">Couldn't load archived spaces — there's no live API in this environment yet.</Alert>
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Archived spaces
      </Typography>

      {data.archivedSpaces.length === 0 ? (
        <Typography color="text.secondary">No archived spaces.</Typography>
      ) : (
        <List>
          {data.archivedSpaces.map((space) => (
            <ListItem
              key={space.key}
              divider
              secondaryAction={
                <Button
                  size="small"
                  onClick={async () => {
                    await restoreSpace({ input: { spaceKey: space.key } })
                    refetch({ requestPolicy: 'network-only' })
                  }}
                >
                  Restore
                </Button>
              }
            >
              <ListItemText
                primary={space.name}
                secondary={space.archivedAtUtc ? `Archived ${new Date(space.archivedAtUtc).toLocaleString()}` : undefined}
              />
            </ListItem>
          ))}
        </List>
      )}
    </Stack>
  )
}
