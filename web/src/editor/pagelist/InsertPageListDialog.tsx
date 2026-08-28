import { useState } from 'react'
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormLabel,
  MenuItem,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material'
import { visuallyHidden } from '@mui/utils'
import { usePageListVocabularyQuery, useParseRqlQuery } from '../../graphql/generated/graphql'
import type { PageListSpec } from '../../pagelist/fenceBody'
import { buildRqlFromBuilder, type PageListBuilderState, type PageListSort } from '../../pagelist/rqlBuilder'
import { querySpans } from '../../pagelist/rqlErrorSpans'
import { useDebouncedValue } from '../useDebouncedValue'

export interface InsertPageListDialogProps {
  open: boolean
  onClose: () => void
  onInsert: (spec: PageListSpec) => void
}

const VALIDATE_DEBOUNCE_MS = 300

const SORT_OPTIONS: { value: PageListSort; label: string }[] = [
  // "Recently updated" is the server's default and prints no ORDER BY (§22.2),
  // so the option is honest about the ordering without writing a clause the
  // author did not ask for.
  { value: 'default', label: 'Recently updated (default)' },
  { value: 'updated-asc', label: 'Least recently updated' },
  { value: 'created-desc', label: 'Newest first' },
  { value: 'created-asc', label: 'Oldest first' },
  { value: 'title-asc', label: 'Title A–Z' },
]

const EMPTY_BUILDER: PageListBuilderState = { labels: [], labelMode: 'all', spaceKeys: [], sort: 'default' }

/**
 * Insert affordance for the ` ```page-list ` fence (design.md §22).
 *
 * Two ways in, one output. The **builder** covers the asked-for case — pages
 * carrying labels, optionally narrowed to spaces, in a chosen order — and the
 * **query** field is the escape hatch for everything else the grammar can
 * express. They are not separate features: the builder prints RQL, switching
 * to the query field hands over exactly what it printed, and both go through
 * the same validation below. There is deliberately no third path that skips
 * the parser.
 *
 * **What gets stored is `canonical`, never the typed text** (§22.6). The
 * canonical printer is a fixed point, so a query stored through it reprints
 * identically on every later edit; storing raw text would let the same query
 * drift a little each time the dialog reopened, and the fence's byte-exact
 * round trip (§4) is what would notice.
 *
 * Validation is `parseRql`, which executes nothing and touches no page data —
 * that is what makes it safe to call on every keystroke. It also means its
 * errors are about syntax and vocabulary and never about existence (§22.4):
 * this dialog cannot and must not tell an author whether a space key names
 * anything real.
 */
export function InsertPageListDialog({ open, onClose, onInsert }: InsertPageListDialogProps) {
  const [mode, setMode] = useState<'builder' | 'query'>('builder')
  const [builder, setBuilder] = useState<PageListBuilderState>(EMPTY_BUILDER)
  const [rawQuery, setRawQuery] = useState('')
  const [limit, setLimit] = useState('')
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) {
      setMode('builder')
      setBuilder(EMPTY_BUILDER)
      setRawQuery('')
      setLimit('')
    }
  }

  const [{ data: vocabulary }] = usePageListVocabularyQuery()
  const spaces = vocabulary?.spaces ?? []
  const labelOptions = vocabulary?.labels ?? []

  const query = mode === 'builder' ? buildRqlFromBuilder(builder) : rawQuery
  const debouncedQuery = useDebouncedValue(query, VALIDATE_DEBOUNCE_MS)
  const [{ data: parseData, fetching: parsing }] = useParseRqlQuery({
    variables: { query: debouncedQuery },
    pause: debouncedQuery.trim().length === 0,
  })
  // Only trust a result that belongs to the text on screen: the debounce means
  // the last response can describe a query the author has already changed, and
  // showing "valid" for superseded text is how a bad query gets inserted.
  const settled = query === debouncedQuery && !parsing
  const parsed = settled ? (parseData?.parseRql ?? null) : null
  const errors = parsed?.errors ?? []

  const limitValid = limit.length === 0 || (/^\d+$/.test(limit) && Number(limit) > 0)
  const canInsert = query.trim().length > 0 && parsed?.isValid === true && limitValid

  const handleInsert = () => {
    if (!canInsert || parsed?.canonical == null) return
    const spec: PageListSpec = { query: parsed.canonical }
    if (limit.length > 0) spec.limit = Number(limit)
    onInsert(spec)
    onClose()
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Insert page list</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={mode}
            onChange={(_e, next: 'builder' | 'query' | null) => {
              if (next === null || next === mode) return
              // Handing the builder's output over on the way to the query
              // field makes "start simple, then refine" a continuous edit
              // rather than a retype from scratch.
              if (next === 'query') setRawQuery(query)
              setMode(next)
            }}
            aria-label="How to define the list"
          >
            <ToggleButton value="builder">Build a filter</ToggleButton>
            <ToggleButton value="query">Write a query</ToggleButton>
          </ToggleButtonGroup>

          {mode === 'builder' ? (
            <>
              <Autocomplete
                multiple
                freeSolo
                options={labelOptions}
                value={builder.labels}
                onChange={(_e, labels: string[]) => setBuilder((b) => ({ ...b, labels }))}
                renderValue={(labels: readonly string[], getItemProps) =>
                  labels.map((label, index) => <Chip size="small" label={label} {...getItemProps({ index })} key={label} />)
                }
                renderInput={(params) => (
                  <TextField {...params} label="Labels" helperText="Pick existing labels, or type one and press Enter." />
                )}
              />
              <FormControl>
                <FormLabel id="page-list-label-mode" sx={{ fontSize: '0.75rem' }}>
                  Match pages with
                </FormLabel>
                <ToggleButtonGroup
                  size="small"
                  exclusive
                  value={builder.labelMode}
                  onChange={(_e, labelMode: 'all' | 'any' | null) => {
                    if (labelMode === null) return
                    setBuilder((b) => ({ ...b, labelMode }))
                  }}
                  aria-labelledby="page-list-label-mode"
                  sx={{ mt: 0.5 }}
                >
                  <ToggleButton value="all">All of these labels (AND)</ToggleButton>
                  <ToggleButton value="any">Any of these labels (OR)</ToggleButton>
                </ToggleButtonGroup>
              </FormControl>
              <Autocomplete
                multiple
                options={spaces.map((space) => space.key)}
                getOptionLabel={(key) => {
                  const space = spaces.find((s) => s.key === key)
                  return space ? `${space.key} — ${space.name}` : key
                }}
                value={builder.spaceKeys}
                onChange={(_e, spaceKeys: string[]) => setBuilder((b) => ({ ...b, spaceKeys }))}
                renderValue={(keys: readonly string[], getItemProps) =>
                  keys.map((key, index) => <Chip size="small" label={key} {...getItemProps({ index })} key={key} />)
                }
                renderInput={(params) => (
                  <TextField {...params} label="Spaces" helperText="Leave empty to search every space you can see." />
                )}
              />
              <TextField
                label="Sort by"
                select
                value={builder.sort}
                onChange={(e) => setBuilder((b) => ({ ...b, sort: e.target.value as PageListSort }))}
                fullWidth
              >
                {SORT_OPTIONS.map((option) => (
                  <MenuItem key={option.value} value={option.value}>
                    {option.label}
                  </MenuItem>
                ))}
              </TextField>
            </>
          ) : (
            <TextField
              label="Query"
              value={rawQuery}
              onChange={(e) => setRawQuery(e.target.value)}
              fullWidth
              multiline
              minRows={2}
              slotProps={{ htmlInput: { spellCheck: false } }}
              helperText={'Filter by label, space, title, created, updated or creator — e.g. label = "safety" AND space IN ("ENG")'}
            />
          )}

          <TextField
            label="Maximum pages to list"
            value={limit}
            onChange={(e) => setLimit(e.target.value)}
            fullWidth
            error={!limitValid}
            helperText={limitValid ? 'Optional. The server caps a page list at 100 results.' : 'A whole number above zero.'}
          />

          <QueryStatus query={query} parsing={!settled && query.trim().length > 0} parsed={parsed} errors={errors} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" onClick={handleInsert} disabled={!canInsert}>
          Insert
        </Button>
      </DialogActions>
    </Dialog>
  )
}

/**
 * The live read-out: the query as it will be stored, with every stretch an
 * error points at marked, and the server's own messages underneath.
 *
 * Announced politely (`aria-live="polite"`) rather than as an alert, because
 * it updates while the author types and an assertive region would interrupt
 * them mid-word. The marked spans carry a visually-hidden lead-in as well as
 * the highlight — §22.5's "underlines them at once" has to survive not being
 * able to see the underline (WCAG 1.4.1).
 */
function QueryStatus({
  query,
  parsing,
  parsed,
  errors,
}: {
  query: string
  parsing: boolean
  parsed: { isValid: boolean; canonical?: string | null } | null
  errors: readonly { code: string; message: string; offset: number; length: number }[]
}) {
  if (query.trim().length === 0) {
    return (
      <Typography variant="caption" color="text.secondary" aria-live="polite">
        Pick a label or a space to build a query — an empty filter lists nothing.
      </Typography>
    )
  }

  if (parsing) {
    return (
      <Typography variant="caption" color="text.secondary" aria-live="polite">
        Checking the query…
      </Typography>
    )
  }

  if (parsed?.isValid === true) {
    return (
      <Alert severity="success" aria-live="polite">
        <Typography variant="caption" component="p">
          This is what will be stored:
        </Typography>
        <Box component="code" sx={{ fontSize: '0.8125rem', wordBreak: 'break-word' }}>
          {parsed.canonical ?? query}
        </Box>
      </Alert>
    )
  }

  return (
    <Alert severity="error" aria-live="polite">
      <Box component="p" sx={{ m: 0, mb: 1, fontSize: '0.8125rem', wordBreak: 'break-word', fontFamily: 'monospace' }}>
        {querySpans(query, errors).map((span, i) =>
          span.isError ? (
            <Box
              key={i}
              component="mark"
              sx={{ bgcolor: 'transparent', color: 'inherit', textDecoration: 'underline wavy', fontWeight: 700 }}
            >
              <Box component="span" sx={visuallyHidden}>
                problem starts:{' '}
              </Box>
              {span.text}
              <Box component="span" sx={visuallyHidden}>
                {' '}
                problem ends
              </Box>
            </Box>
          ) : (
            <Box component="span" key={i}>
              {span.text}
            </Box>
          ),
        )}
      </Box>
      <Box component="ul" sx={{ m: 0, pl: 2.5, fontSize: '0.8125rem' }}>
        {errors.map((error, i) => (
          // The server's message, verbatim. §22.3 gives a refused
          // classification field its own code and its own wording precisely so
          // an author learns the rule once — rewriting it here would undo that.
          <li key={`${error.code}-${error.offset}-${i}`}>{error.message}</li>
        ))}
      </Box>
    </Alert>
  )
}
