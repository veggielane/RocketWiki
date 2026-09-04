import { Autocomplete, Chip, TextField } from '@mui/material'
import type { SelectorValue } from '../markings/markingRefusal'

/** One configured category (`Query.selectorCategories`), as the picker needs it. */
export interface SelectorCategoryOption {
  name: string
  description?: string | null
  values: readonly string[]
}

export interface SelectorValuePickerProps {
  /** The instance's configured categories — the whole vocabulary; nothing here is typed by hand. */
  categories: readonly SelectorCategoryOption[]
  value: readonly SelectorValue[]
  onChange: (next: SelectorValue[]) => void
  disabled?: boolean
  label?: string
  helperText?: string
}

interface Option extends SelectorValue {
  /** A value the grant carries that no configured category offers — kept so it stays visible and removable. */
  unconfigured: boolean
}

function sameSelector(a: SelectorValue, b: SelectorValue): boolean {
  return a.category === b.category && a.value === b.value
}

/**
 * The selector values an ACCESS GRANT confers (design.md §21.15, §6.4): a
 * chip multi-select whose options are grouped by category. Several values
 * per category are allowed here — a grant may confer both APPLE and BANANA
 * — which is the one way this differs from a page's marking, where a
 * category holds at most one value (that control is a select per category
 * in markings/PageMarkingSection.tsx, not this picker).
 *
 * A value the grant already carries that no configured category offers (the
 * category was removed from configuration, or the grant arrived by import)
 * is kept in the options so it stays visible and removable; it is not
 * silently dropped, and it is not offered to any grant that does not
 * already hold it.
 */
export function SelectorValuePicker({
  categories,
  value,
  onChange,
  disabled = false,
  label = 'Selector values',
  helperText,
}: SelectorValuePickerProps) {
  const configured: Option[] = categories.flatMap((category) =>
    category.values.map((v) => ({ category: category.name, value: v, unconfigured: false })),
  )
  const carriedOnly: Option[] = value
    .filter((held) => !configured.some((option) => sameSelector(option, held)))
    .map((held) => ({ ...held, unconfigured: true }))
  const options = [...configured, ...carriedOnly]
  const selected = options.filter((option) => value.some((held) => sameSelector(option, held)))
  const nothingToOffer = options.length === 0

  return (
    <Autocomplete
      multiple
      size="small"
      disabled={disabled || nothingToOffer}
      options={options}
      value={selected}
      groupBy={(option) => option.category}
      getOptionLabel={(option) => option.value}
      isOptionEqualToValue={(option, chosen) => sameSelector(option, chosen)}
      onChange={(_event, next) => onChange(next.map(({ category, value: v }) => ({ category, value: v })))}
      renderValue={(chosen, getItemProps) =>
        chosen.map((option, index) => {
          const { key, ...itemProps } = getItemProps({ index })
          return (
            <Chip
              size="small"
              // Category AND value on the chip: two categories may share a
              // value name, and a chip reading just "NORTH" would not say
              // which one it confers.
              label={`${option.category}: ${option.value}`}
              key={key}
              {...itemProps}
            />
          )
        })
      }
      renderInput={(params) => (
        <TextField
          {...params}
          label={label}
          helperText={
            nothingToOffer
              ? 'No selector categories are configured on this instance, so a grant here carries no selector values.'
              : (helperText ?? 'The values this grant confers. Leave empty for a grant that carries none.')
          }
        />
      )}
    />
  )
}
