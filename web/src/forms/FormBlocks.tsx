import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  MenuItem,
  Paper,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import {
  useCreatePageEntryMutation,
  usePageEntriesQuery,
  usePageFormsQuery,
  type FormFieldType,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { useCurrentPageId } from '../pages/pageContext'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'

/**
 * The two form fences (docs/ENTRIES-AND-FORMS-PLAN.md).
 *
 * Both read their definition from `pageForms`, which the SERVER parses out of the page's
 * Markdown. The SPA deliberately never parses a `form-definition` fence itself: rendering
 * and validation would then be two opinions about what a form is, agreeing right until
 * they did not. Same rule §22's RQL follows.
 *
 * Neither block ever reports how many records were withheld. An entry the reader may not
 * see is simply absent, and a count of the difference would be a census of the classified
 * estate — the reason RQL refuses aggregates and the analytics report never buckets by
 * level.
 */

/** Maps a field type to an input. `date` uses the native control rather than a picker
 *  component: it needs no dependency, and it is already localized by the browser. */
function fieldInputType(type: FormFieldType): string {
  switch (type) {
    case 'NUMBER':
      return 'number'
    case 'DATE':
      return 'date'
    default:
      return 'text'
  }
}

function useDefinition(collection: string) {
  const pageId = useCurrentPageId()
  const [{ data, fetching }] = usePageFormsQuery({ variables: { pageId: pageId ?? '' }, pause: !pageId })
  const wanted = collection.trim().toLowerCase()
  return {
    pageId,
    fetching,
    definition: data?.pageForms?.definitions.find((d) => d.collection.trim().toLowerCase() === wanted) ?? null,
    error: data?.pageForms?.errors.find((e) => e.collection.trim().toLowerCase() === wanted) ?? null,
  }
}

/**
 * ```` ```form-definition ```` — renders the form it declares, so the fence that DEFINES
 * a form is also the place you fill one in. One block rather than a separate "submit"
 * macro: a definition nobody can submit against is a schema, not a form.
 */
export function FormDefinitionBlock({ collection }: { collection: string }) {
  const { pageId, fetching, definition, error } = useDefinition(collection)
  const [values, setValues] = useState<Record<string, string>>({})
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [{ fetching: submitting }, createEntry] = useCreatePageEntryMutation()

  if (!pageId) return <Alert severity="info">This form only works on a saved page.</Alert>
  if (fetching) return <Skeleton variant="rectangular" height={140} />
  // The server's own parse error, shown verbatim: it names the collection and the line,
  // which is what the author needs. A form that silently vanished would be worse.
  if (error) return <Alert severity="warning">{error.message}</Alert>
  if (!definition) {
    return <Alert severity="info">No form named "{collection}" is defined on this page.</Alert>
  }

  const missing = definition.fields.filter((f) => f.required && !values[f.name]?.trim())

  const handleSubmit = async () => {
    setSubmitError(null)
    setSaved(false)
    const result = await createEntry({
      input: { pageId, collection: definition.collection, data: JSON.stringify(values) },
    })
    if (result.error) {
      setSubmitError("Couldn't save this entry.")
      return
    }
    const refused = describeMutationError(result.data?.createPageEntry.error)
    if (refused) {
      setSubmitError(refused)
      return
    }
    // Cleared rather than left filled: the next submission is a new record, and a form
    // that keeps the last one invites accidental duplicates.
    setValues({})
    setSaved(true)
  }

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Typography variant="subtitle2" component="h3" gutterBottom>
        {definition.collection}
      </Typography>
      {submitError && <Alert severity="error" sx={{ mb: 1 }}>{submitError}</Alert>}
      {saved && <Alert severity="success" sx={{ mb: 1 }}>Saved.</Alert>}
      <Stack spacing={2}>
        {definition.fields.map((field) => (
          <TextField
            key={field.name}
            select={field.type === 'SELECT'}
            type={field.type === 'SELECT' ? undefined : fieldInputType(field.type)}
            label={field.name}
            required={field.required}
            value={values[field.name] ?? ''}
            onChange={(e) => setValues((v) => ({ ...v, [field.name]: e.target.value }))}
            size="small"
            fullWidth
            // A date input needs its label shrunk: the control is never visually
            // empty, so the floating label would sit on top of the placeholder.
            slotProps={field.type === 'DATE' ? { inputLabel: { shrink: true } } : undefined}
          >
            {field.type === 'SELECT' &&
              field.options.map((option) => (
                <MenuItem key={option} value={option}>
                  {option}
                </MenuItem>
              ))}
          </TextField>
        ))}
        <Box>
          <Button
            variant="contained"
            size="small"
            disabled={submitting || missing.length > 0}
            onClick={() => void handleSubmit()}
          >
            Submit
          </Button>
        </Box>
      </Stack>
    </Paper>
  )
}

/**
 * ```` ```form-list ```` — the records of one collection on this page, as a table.
 *
 * Columns come from the definition rather than from the records, so a field nobody has
 * filled in still has a column and the table does not change shape as records arrive.
 */
export function FormListBlock({ collection, columns }: { collection: string; columns: string[] }) {
  const { pageId, fetching, definition, error } = useDefinition(collection)
  const [{ data, fetching: loadingEntries }] = usePageEntriesQuery({
    variables: { pageId: pageId ?? '', collection },
    pause: !pageId,
  })

  if (!pageId) return <Alert severity="info">This list only works on a saved page.</Alert>
  if (fetching || loadingEntries) return <Skeleton variant="rectangular" height={120} />
  if (error) return <Alert severity="warning">{error.message}</Alert>
  if (!definition) {
    // Distinct from "no records yet" on purpose: those are different facts and a reader
    // acts on them differently.
    return <Alert severity="info">No form named "{collection}" is defined on this page.</Alert>
  }

  const shown = columns.length > 0
    ? definition.fields.filter((f) => columns.includes(f.name))
    : definition.fields
  const entries = data?.pageEntries ?? []

  if (entries.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary">
        No records yet.
      </Typography>
    )
  }

  return (
    <Table size="small" aria-label={`${definition.collection} records`}>
      <TableHead>
        <TableRow>
          {shown.map((field) => (
            <TableCell key={field.name}>{field.name}</TableCell>
          ))}
          <TableCell>Marking</TableCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {entries.map((entry) => {
          // A record whose JSON will not parse is shown as a blank row rather than
          // taking the table down — the column set is the definition's, not the row's.
          let fields: Record<string, unknown> = {}
          try {
            const parsed: unknown = JSON.parse(entry.data)
            if (parsed && typeof parsed === 'object') fields = parsed as Record<string, unknown>
          } catch {
            fields = {}
          }
          return (
            <TableRow key={entry.id}>
              {shown.map((field) => (
                <TableCell key={field.name}>{String(fields[field.name] ?? '')}</TableCell>
              ))}
              <TableCell>
                {/* Per record, not per table: entries on one page can legitimately
                    carry different markings, and a single label over the set would
                    tell a reader the wrong thing about every row but one. */}
                <MarkingLevelBadge level={entry.marking.level} levelName={entry.marking.levelName} />
              </TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
  )
}
