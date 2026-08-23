import { useState } from 'react'
import { Autocomplete, Button, Chip, Stack, TextField } from '@mui/material'

export interface LabelEditorProps {
  labels: string[]
  knownLabels: string[]
  onSave: (labels: string[]) => Promise<void>
  onCancel: () => void
}

/**
 * Space-scoped label editor: freeSolo Autocomplete so a new label can be
 * typed, with existing ones offered as suggestions.
 *
 * NOTE (schema reconciliation): currently unmounted. The real API's label
 * mutations (attachLabel/detachLabel) take label *ids*, but no query
 * exposes them — `labels(spaceKey)` and `Page.labels` return names only —
 * so a full edit round-trip is impossible (reported contract gap).
 * PageViewPage shows labels read-only until an id read path exists; this
 * component is kept ready for that day.
 */
export function LabelEditor({ labels, knownLabels, onSave, onCancel }: LabelEditorProps) {
  const [value, setValue] = useState<string[]>(labels)
  const [saving, setSaving] = useState(false)

  const handleSave = async () => {
    setSaving(true)
    try {
      await onSave(value)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
      <Autocomplete
        multiple
        freeSolo
        size="small"
        options={knownLabels}
        value={value}
        onChange={(_e, newValue) => setValue(newValue)}
        renderValue={(tagValue, getItemProps) =>
          tagValue.map((option, index) => {
            const { key, ...itemProps } = getItemProps({ index })
            return <Chip size="small" label={option} key={key} {...itemProps} />
          })
        }
        renderInput={(params) => <TextField {...params} label="Labels" placeholder="Add a label…" />}
        sx={{ minWidth: 280, flexGrow: 1 }}
      />
      <Button size="small" variant="contained" disabled={saving} onClick={() => void handleSave()}>
        Save
      </Button>
      <Button size="small" onClick={onCancel} disabled={saving}>
        Cancel
      </Button>
    </Stack>
  )
}
