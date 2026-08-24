import { useState } from 'react'
import {
  Alert,
  Autocomplete,
  Button,
  Chip,
  IconButton,
  Paper,
  Skeleton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import { useEffectivePermissionQuery, type InspectedPrincipalInput } from '../../graphql/generated/graphql'
import { useIsInstanceAdmin } from '../../auth/useIsInstanceAdmin'
import { toEffectivePermissionDetail } from './mapEffectivePermission'
import { PermissionInspector } from './PermissionInspector'
import { describeLoadFailure } from '../../feedback/unavailableCopy'

interface AttributeRowDraft {
  key: string
  values: string[]
}

export interface PermissionInspectorPanelProps {
  pageId: string
  /**
   * Offer the foreign/what-if subject form. The form is additionally
   * hidden from non-instance-admins (the server refuses a `subject` from
   * anyone else regardless — this just avoids offering what will be
   * refused). Self-inspection is always available to anyone who can view
   * the page.
   */
  allowSubjectInput?: boolean
}

/**
 * design.md §6.6's shipped inspector, wired to the root
 * `effectivePermission(pageId, subject?)` query. No `subject` means "why
 * can/can't *I*" — available to any viewer. A principal-shaped `subject`
 * is the instance-admin-only foreign/what-if mode: no other user's token
 * exists to build a real Principal from, so the admin describes one
 * (userId, groups, attributes) and the engine evaluates exactly that.
 * The caller's own canView still gates every mode, and every inspection
 * is audited server-side as `permission.inspect`.
 */
export function PermissionInspectorPanel({ pageId, allowSubjectInput = false }: PermissionInspectorPanelProps) {
  const { isInstanceAdmin } = useIsInstanceAdmin()
  const [subject, setSubject] = useState<InspectedPrincipalInput | null>(null)
  const [{ data, fetching, error }] = useEffectivePermissionQuery({ variables: { pageId, subject } })

  const [userId, setUserId] = useState('')
  const [groups, setGroups] = useState<string[]>([])
  const [attributeRows, setAttributeRows] = useState<AttributeRowDraft[]>([])

  const showSubjectForm = allowSubjectInput && isInstanceAdmin
  const detail = data?.effectivePermission ? toEffectivePermissionDetail(data.effectivePermission) : null

  const inspectSubject = () => {
    const trimmedUserId = userId.trim()
    if (trimmedUserId.length === 0) return
    setSubject({
      userId: trimmedUserId,
      groups,
      attributes: attributeRows
        .filter((row) => row.key.trim().length > 0)
        .map((row) => ({ key: row.key.trim(), values: row.values })),
    })
  }

  return (
    <Stack spacing={2}>
      {showSubjectForm && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Typography variant="subtitle2" gutterBottom>
            Inspect another principal (what-if)
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1.5 }}>
            Describe a principal to test — the rule engine evaluates exactly what you enter, not the user's
            live token. Every inspection is audited.
          </Typography>
          <Stack spacing={1.5}>
            <TextField
              label="User ID (token subject)"
              size="small"
              value={userId}
              onChange={(e) => setUserId(e.target.value)}
            />
            <Autocomplete
              multiple
              freeSolo
              size="small"
              options={[]}
              value={groups}
              onChange={(_e, value) => setGroups(value)}
              renderValue={(tagValue, getItemProps) =>
                tagValue.map((option, index) => {
                  const { key, ...itemProps } = getItemProps({ index })
                  return <Chip size="small" label={option} key={key} {...itemProps} />
                })
              }
              renderInput={(params) => <TextField {...params} label="Groups" placeholder="Add a group…" />}
            />
            {attributeRows.map((row, index) => (
              <Stack key={index} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                <TextField
                  label="Attribute key"
                  size="small"
                  value={row.key}
                  onChange={(e) =>
                    setAttributeRows((prev) => prev.map((r, i) => (i === index ? { ...r, key: e.target.value } : r)))
                  }
                />
                <Autocomplete
                  multiple
                  freeSolo
                  size="small"
                  options={[]}
                  value={row.values}
                  onChange={(_e, value) =>
                    setAttributeRows((prev) => prev.map((r, i) => (i === index ? { ...r, values: value } : r)))
                  }
                  renderValue={(tagValue, getItemProps) =>
                    tagValue.map((option, valueIndex) => {
                      const { key, ...itemProps } = getItemProps({ index: valueIndex })
                      return <Chip size="small" label={option} key={key} {...itemProps} />
                    })
                  }
                  renderInput={(params) => <TextField {...params} label="Values" placeholder="Add a value…" />}
                  sx={{ minWidth: 220, flexGrow: 1 }}
                />
                <Tooltip title="Remove attribute">
                  <IconButton
                    size="small"
                    aria-label="Remove attribute"
                    onClick={() => setAttributeRows((prev) => prev.filter((_r, i) => i !== index))}
                  >
                    <DeleteOutlineIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </Stack>
            ))}
            <Stack direction="row" spacing={1}>
              <Button
                size="small"
                startIcon={<AddIcon />}
                onClick={() => setAttributeRows((prev) => [...prev, { key: '', values: [] }])}
              >
                Add attribute
              </Button>
              <Button size="small" variant="contained" disabled={userId.trim().length === 0} onClick={inspectSubject}>
                Inspect
              </Button>
              {subject !== null && (
                <Button size="small" onClick={() => setSubject(null)}>
                  Back to myself
                </Button>
              )}
            </Stack>
          </Stack>
        </Paper>
      )}

      {fetching && <Skeleton variant="rectangular" height={160} />}

      {!fetching && (error || !detail) && (
        // Null covers "page not viewable to you" exactly like "no page" —
        // absent, not forbidden (design.md §6.7) — and the audited refusal
        // of a foreign subject from a non-admin.
        <Alert severity="info">
          {describeLoadFailure({ kind: 'PERMISSION_INSPECTION', forPrincipal: subject !== null }).summary}
        </Alert>
      )}

      {!fetching && detail && (
        <>
          {subject !== null && (
            <Alert severity="warning" variant="outlined">
              What-if result for the principal described above — not the live token of any real user.
            </Alert>
          )}
          <PermissionInspector detail={detail} />
        </>
      )}
    </Stack>
  )
}
