import { useMemo, useState } from 'react'
import {
  Alert,
  Autocomplete,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import {
  computeVisibilityChange,
  type MoveTargetOption,
  type RestrictionSummary,
} from '../access/move/visibilityChange'
import { RuleExpressionOrUnreadable } from '../access/RuleExpressionSummary'

export type { MoveTargetOption }

export interface MovePageDialogProps {
  open: boolean
  onClose: () => void
  pageTitle: string
  /** The restrictions the page currently inherits from its current parent chain (ancestorRestrictionsOf). */
  currentAncestorRestrictions: RestrictionSummary[]
  /** Candidate new parents (typically the space's page tree, minus the page's own subtree). */
  targetOptions: MoveTargetOption[]
  onConfirm: (targetParentId: string | null) => void
}

/**
 * design.md §6.4: "Moving a page re-evaluates nothing — restrictions are
 * positional... The move UI warns when a move would change who can see a
 * page." The warning is driven by `computeVisibilityChange`
 * (access/move/visibilityChange.ts), not re-derived here.
 */
export function MovePageDialog({
  open,
  onClose,
  pageTitle,
  currentAncestorRestrictions,
  targetOptions,
  onConfirm,
}: MovePageDialogProps) {
  const [selectedId, setSelectedId] = useState<string | 'root' | null>(null)

  const selectedOption = targetOptions.find((t) => (t.id ?? 'root') === selectedId) ?? null

  const change = useMemo(() => {
    if (!selectedOption) return null
    return computeVisibilityChange(currentAncestorRestrictions, selectedOption.ancestorRestrictions)
  }, [currentAncestorRestrictions, selectedOption])

  const handleConfirm = () => {
    if (!selectedOption) return
    onConfirm(selectedOption.id)
    setSelectedId(null)
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Move "{pageTitle}"</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>Choose the new parent page.</DialogContentText>

        <Autocomplete
          options={targetOptions}
          getOptionLabel={(t) => t.title}
          onChange={(_e, value) => setSelectedId(value ? (value.id ?? 'root') : null)}
          renderInput={(params) => <TextField {...params} label="New parent" autoFocus />}
        />

        {change?.changed && (
          <Alert severity="warning" sx={{ mt: 2 }}>
            <Typography variant="body2" sx={{ fontWeight: 600, mb: 1 }}>
              This move changes who can see this page.
            </Typography>
            <Stack spacing={1.5}>
              {change.added.length > 0 && (
                <Stack spacing={0.5}>
                  <Typography variant="caption">
                    New restriction{change.added.length > 1 ? 's' : ''} would start applying (inherited from{' '}
                    {selectedOption?.title}):
                  </Typography>
                  {change.added.map((r) => (
                    <RuleExpressionOrUnreadable key={r.ruleId} node={r.expression} />
                  ))}
                </Stack>
              )}
              {change.removed.length > 0 && (
                <Stack spacing={0.5}>
                  <Typography variant="caption">
                    Restriction{change.removed.length > 1 ? 's' : ''} that currently apply would no longer:
                  </Typography>
                  {change.removed.map((r) => (
                    <RuleExpressionOrUnreadable key={r.ruleId} node={r.expression} />
                  ))}
                </Stack>
              )}
            </Stack>
          </Alert>
        )}

        {selectedOption && change && !change.changed && (
          <Alert severity="success" variant="outlined" sx={{ mt: 2 }}>
            No change to who can see this page.
          </Alert>
        )}

      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          onClick={handleConfirm}
          disabled={!selectedOption}
          variant="contained"
          color={change?.changed ? 'warning' : 'primary'}
        >
          {change?.changed ? 'Move anyway' : 'Move'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
