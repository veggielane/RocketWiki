import { useState } from 'react'
import { Autocomplete, Stack, TextField } from '@mui/material'
import { useUserDirectoryQuery, type UserDirectoryQuery } from '../graphql/generated/graphql'
import { useDebouncedValue } from '../useDebouncedValue'
import { UserAvatar } from '../avatars/UserAvatar'

export type DirectoryUser = NonNullable<NonNullable<UserDirectoryQuery['userDirectory']>['nodes']>[number]

/** The same wait the search screen and the page-list dialog use — one typing cadence, not a third. */
const SEARCH_DEBOUNCE_MS = 300

/**
 * How many names to offer at once. Well under the connection's cap; a picker is
 * for choosing, not browsing — and the cap is asserted against the schema in
 * pagedFieldLimits.test.ts, so it cannot drift past what the field allows.
 */
export const DIRECTORY_PAGE_SIZE = 20

export interface UserDirectoryPickerProps {
  label: string
  value: DirectoryUser | null
  onChange: (user: DirectoryUser | null) => void
  disabled?: boolean
  helperText?: string
}

/**
 * Choosing a person, from the directory every signed-in user may read.
 *
 * The directory carries identity and nothing else — id, display name, avatar —
 * so this control can answer "which person do you mean" and cannot become a
 * second place to learn things about colleagues.
 *
 * It opens with names already in it: a blank search returns the roster's first
 * page, which is friendlier than an empty box that gives nothing back until you
 * guess a spelling. Typing narrows it, debounced at the app's one typing
 * cadence so a name is one request rather than one per keystroke.
 *
 * **Two people can share a display name and the directory cannot tell them
 * apart** — that is the cost of a picker with no email in it, and the honest
 * place to note it is here rather than in a comment nobody finds when it bites.
 */
export function UserDirectoryPicker({ label, value, onChange, disabled, helperText }: UserDirectoryPickerProps) {
  const [typed, setTyped] = useState('')
  const search = useDebouncedValue(typed, SEARCH_DEBOUNCE_MS)
  const [{ data, fetching }] = useUserDirectoryQuery({
    variables: { search: search.trim().length > 0 ? search.trim() : undefined, first: DIRECTORY_PAGE_SIZE },
  })
  const options = data?.userDirectory?.nodes ?? []

  return (
    <Autocomplete
      size="small"
      disabled={disabled}
      value={value}
      onChange={(_event, next) => onChange(next)}
      inputValue={typed}
      onInputChange={(_event, next) => setTyped(next)}
      options={options}
      loading={fetching}
      getOptionLabel={(option) => option.displayName}
      isOptionEqualToValue={(option, selected) => option.id === selected.id}
      // The server has already matched on display name; filtering again here
      // would hide results the search deliberately returned.
      filterOptions={(all) => all}
      noOptionsText={fetching ? 'Searching…' : 'No matching people'}
      renderOption={(props, option) => {
        const { key, ...rest } = props as typeof props & { key: string }
        return (
          <Stack key={key} component="li" {...rest} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <UserAvatar userId={option.id} hasAvatar={option.hasAvatar} displayName={option.displayName} size={24} />
            <span>{option.displayName}</span>
          </Stack>
        )
      }}
      renderInput={(params) => <TextField {...params} label={label} helperText={helperText} />}
      sx={{ minWidth: 280 }}
    />
  )
}
