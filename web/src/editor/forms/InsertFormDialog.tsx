import { useState } from 'react'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  IconButton,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
  FormControlLabel,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlineOutlined'
import AddIcon from '@mui/icons-material/Add'
import type { FormFieldType } from '../../graphql/generated/graphql'
import {
  buildFormDefinitionFenceBody,
  buildFormListFenceBody,
  type FormFieldDraft,
} from '../../forms/fenceBody'
import { useDialogFullScreen } from '../../app/useDialogFullScreen'
import { InsertRequirements } from '../InsertRequirements'

const FIELD_TYPES: { value: FormFieldType; label: string }[] = [
  { value: 'TEXT', label: 'Text' },
  { value: 'NUMBER', label: 'Number' },
  { value: 'DATE', label: 'Date' },
  { value: 'SELECT', label: 'Choice' },
]

const emptyField = (): FormFieldDraft => ({ name: '', type: 'TEXT', required: false, options: '' })

/**
 * Builds a `form-definition` fence, and optionally the `form-list` that shows
 * its records.
 *
 * The field syntax is the part nobody guesses — `name: select(a, b), required`
 * is not discoverable by trying. Without this the only way to add a form was to
 * type the fence from memory, which is the difference between a feature people
 * can find and one only its author knows how to use.
 *
 * It writes the fence and stops there. The definition is then ordinary page
 * content, parsed by the server exactly as if it had been typed, and editing it
 * later means editing the text — there is deliberately no round trip back into
 * this dialog, which would make it a second editor for the same thing.
 */
export function InsertFormDialog({
  open,
  onClose,
  onInsert,
}: {
  open: boolean
  onClose: () => void
  /** Called with each fence body to insert, in order. */
  onInsert: (fences: { language: 'form-definition' | 'form-list'; body: string }[]) => void
}) {
  const fullScreen = useDialogFullScreen()
  const [collection, setCollection] = useState('')
  const [fields, setFields] = useState<FormFieldDraft[]>([emptyField()])
  const [alsoList, setAlsoList] = useState(true)

  // Reset on OPEN, not on close. Resetting on the way out is the same outcome
  // only as long as every exit runs through a handler that remembers to do it —
  // and the family's one real bug was exactly that gap (CreatePageDialog's
  // success path). Adjusting during render against a tracked copy is the idiom
  // the other four fence dialogs already use, and it survives a programmatic
  // close, so a reopen is always a fresh form by construction.
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) {
      setCollection('')
      setFields([emptyField()])
      setAlsoList(true)
    }
  }

  const named = fields.filter((f) => f.name.trim().length > 0)
  const selectsWithoutOptions = named.filter(
    (f) => f.type === 'SELECT' && f.options.split(',').every((o) => o.trim().length === 0),
  )
  const duplicate = named.length !== new Set(named.map((f) => f.name.trim().toLowerCase())).size
  // The two headline requirements were the only ones with nowhere to appear:
  // the duplicate-name and empty-choices problems already had Alerts, so a
  // blank collection or an unnamed field was the case where Insert simply died
  // in silence.
  const missing = [
    ...(collection.trim().length === 0 ? ['a collection name'] : []),
    ...(named.length === 0 ? ['at least one named field'] : []),
  ]
  const canInsert = missing.length === 0 && selectsWithoutOptions.length === 0 && !duplicate

  const handleInsert = () => {
    const draft = { collection, fields: named }
    const fences: { language: 'form-definition' | 'form-list'; body: string }[] = [
      { language: 'form-definition', body: buildFormDefinitionFenceBody(draft) },
    ]
    if (alsoList) {
      // Every column, no filter: the author can narrow it afterwards, and a
      // guessed filter would be a predicate nobody asked for.
      fences.push({
        language: 'form-list',
        body: buildFormListFenceBody({ collection, columns: [], where: '' }),
      })
    }
    onInsert(fences)
    onClose()
  }

  const update = (index: number, patch: Partial<FormFieldDraft>) =>
    setFields((current) => current.map((f, i) => (i === index ? { ...f, ...patch } : f)))

  return (
    <Dialog
      open={open}
      // A backdrop click must not take an arbitrarily long field list with it.
      // This dialog is the family's tallest and its content is entirely typed;
      // Escape and Cancel remain the ways out, both of which are deliberate.
      onClose={(_event, reason) => {
        if (reason === 'backdropClick') return
        onClose()
      }}
      fullWidth
      maxWidth="sm"
      fullScreen={fullScreen}
    >
      <DialogTitle>Insert form</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            autoFocus
            required
            label="Collection"
            value={collection}
            onChange={(e) => setCollection(e.target.value)}
            fullWidth
            size="small"
            helperText="Names this set of records. It belongs to this page — the same name on another page is a separate set."
          />

          <Divider />
          <Typography variant="subtitle2" component="h3">
            Fields
          </Typography>

          {fields.map((field, index) => (
            <Stack key={index} direction="row" spacing={1} sx={{ alignItems: "flex-start" }}>
              <TextField
                label="Name"
                value={field.name}
                onChange={(e) => update(index, { name: e.target.value })}
                size="small"
                sx={{ flexGrow: 1 }}
              />
              <TextField
                select
                label="Type"
                value={field.type}
                onChange={(e) => update(index, { type: e.target.value as FormFieldType })}
                size="small"
                sx={{ minWidth: 120 }}
              >
                {FIELD_TYPES.map((t) => (
                  <MenuItem key={t.value} value={t.value}>
                    {t.label}
                  </MenuItem>
                ))}
              </TextField>
              {field.type === 'SELECT' && (
                <TextField
                  label="Choices"
                  value={field.options}
                  onChange={(e) => update(index, { options: e.target.value })}
                  size="small"
                  placeholder="low, medium, high"
                  sx={{ flexGrow: 1 }}
                />
              )}
              <FormControlLabel
                control={
                  <Switch
                    size="small"
                    checked={field.required}
                    onChange={(e) => update(index, { required: e.target.checked })}
                  />
                }
                label="Required"
              />
              <Tooltip title="Remove field">
                <span>
                  <IconButton
                    size="small"
                    aria-label={`Remove field ${index + 1}`}
                    disabled={fields.length === 1}
                    onClick={() => setFields((current) => current.filter((_, i) => i !== index))}
                  >
                    <DeleteOutlineIcon fontSize="small" />
                  </IconButton>
                </span>
              </Tooltip>
            </Stack>
          ))}

          <Button
            startIcon={<AddIcon />}
            size="small"
            sx={{ alignSelf: 'flex-start' }}
            onClick={() => setFields((current) => [...current, emptyField()])}
          >
            Add field
          </Button>

          {duplicate && <Alert severity="warning">Two fields share a name.</Alert>}
          {selectsWithoutOptions.length > 0 && (
            <Alert severity="warning">
              A choice field needs at least one option:{' '}
              {selectsWithoutOptions.map((f) => f.name.trim()).join(', ')}.
            </Alert>
          )}

          <Divider />
          <FormControlLabel
            control={<Switch checked={alsoList} onChange={(e) => setAlsoList(e.target.checked)} />}
            label="Also insert a table of the records"
          />
          <InsertRequirements missing={missing} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!canInsert} onClick={handleInsert}>
          Insert
        </Button>
      </DialogActions>
    </Dialog>
  )
}
