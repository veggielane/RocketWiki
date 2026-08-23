import { useState } from 'react'
import { useParams } from 'react-router-dom'
import {
  Alert,
  Button,
  IconButton,
  MenuItem,
  Paper,
  Select,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import AddIcon from '@mui/icons-material/Add'
import {
  useCreateAccessRuleMutation,
  useDeleteAccessRuleMutation,
  useRuleVocabularyQuery,
  useSpaceGrantsQuery,
  useUpdateAccessRuleMutation,
  type SpaceRole,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { AccessGate } from '../auth/AccessGate'
import { RuleBuilder } from '../access/RuleBuilder'
import { parseRuleNode, serializeRuleNode } from '../access/ruleSerializer'
import type { AttributeOption } from '../access/attributeOption'
import type { ValidationResult } from '../access/builderState'
import type { RuleNode } from '../access/ruleTypes'

const ROLE_LABELS: Record<SpaceRole, string> = { VIEWER: 'Viewer', EDITOR: 'Editor', SPACE_ADMIN: 'Space admin' }

interface GrantRow {
  clientId: string
  id: string | null // null = not yet saved
  role: SpaceRole
  initialRole: SpaceRole | null
  initialExpressionJson: string | null
  initialExpression: RuleNode
}

interface SpaceGrantsEditorProps {
  spaceId: string
  initialGrants: { id: string; role: SpaceRole | null; expressionJson: string }[]
  groups: string[]
  attributes: AttributeOption[]
  onSaved: () => void
}

/**
 * design.md §6.4: "multiple grants OR together; your role is the highest
 * whose expression you satisfy" — reuses the same `RuleBuilder` the create-
 * space form does, one per grant, each independently validated before Save
 * is enabled.
 *
 * The real API manages rules individually (createAccessRule/
 * updateAccessRule/deleteAccessRule — an append-only audited history, not a
 * set replacement), so Save diffs the rows against what was loaded: added
 * rows are created, edited rows updated, removed rows deleted.
 *
 * Picker vocabulary (known groups + the attribute registry) comes from the
 * manage-gated `RuleVocabulary` query — suggestions, never authority: the
 * group picker stays freeSolo, so an empty or failed vocabulary query just
 * means typing by hand (design.md §6.6).
 */
function SpaceGrantsEditor({ spaceId, initialGrants, groups, attributes, onSaved }: SpaceGrantsEditorProps) {
  const [, createAccessRule] = useCreateAccessRuleMutation()
  const [, updateAccessRule] = useUpdateAccessRuleMutation()
  const [, deleteAccessRule] = useDeleteAccessRuleMutation()

  const [rows, setRows] = useState<GrantRow[]>(() =>
    initialGrants.map((g) => ({
      clientId: g.id,
      id: g.id,
      role: g.role ?? 'VIEWER',
      initialRole: g.role,
      initialExpressionJson: g.expressionJson,
      initialExpression: parseRuleNode(g.expressionJson),
    })),
  )
  const [removedRuleIds, setRemovedRuleIds] = useState<string[]>([])
  const [validation, setValidation] = useState<Record<string, ValidationResult>>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [saving, setSaving] = useState(false)

  const addGrant = () => {
    const clientId = crypto.randomUUID()
    setRows((prev) => [
      ...prev,
      {
        clientId,
        id: null,
        role: 'VIEWER',
        initialRole: null,
        initialExpressionJson: null,
        initialExpression: { kind: 'group', group: '' },
      },
    ])
  }

  const removeGrant = (clientId: string) => {
    const row = rows.find((r) => r.clientId === clientId)
    if (row?.id) {
      setRemovedRuleIds((prev) => [...prev, row.id!])
    }
    setRows((prev) => prev.filter((r) => r.clientId !== clientId))
    setValidation((prev) => {
      const { [clientId]: _removed, ...rest } = prev
      return rest
    })
  }

  const setRole = (clientId: string, role: SpaceRole) => {
    setRows((prev) => prev.map((r) => (r.clientId === clientId ? { ...r, role } : r)))
  }

  const allValid = rows.length > 0 && rows.every((r) => validation[r.clientId]?.valid)

  const handleSave = async () => {
    if (!allValid || saving) return
    setSaveError(null)
    setSaved(false)
    setSaving(true)
    try {
      for (const row of rows) {
        const v = validation[row.clientId]
        if (!v?.valid) throw new Error('unreachable: allValid already checked')
        const expressionJson = serializeRuleNode(v.node)

        if (row.id === null) {
          const result = await createAccessRule({
            input: { kind: 'SPACE_GRANT', spaceId, role: row.role, expressionJson },
          })
          const errorText = describeMutationError(result.data?.createAccessRule.error)
          if (errorText) {
            setSaveError(errorText)
            return
          }
        } else if (expressionJson !== row.initialExpressionJson || row.role !== row.initialRole) {
          const result = await updateAccessRule({
            input: { accessRuleId: row.id, role: row.role, expressionJson },
          })
          const errorText = describeMutationError(result.data?.updateAccessRule.error)
          if (errorText) {
            setSaveError(errorText)
            return
          }
        }
      }

      for (const ruleId of removedRuleIds) {
        const result = await deleteAccessRule({ input: { accessRuleId: ruleId } })
        const errorText = describeMutationError(result.data?.deleteAccessRule.error)
        if (errorText) {
          setSaveError(errorText)
          return
        }
      }

      setSaved(true)
      onSaved()
    } finally {
      setSaving(false)
    }
  }

  return (
    <Stack spacing={3}>
      <Alert severity="info" variant="outlined">
        Multiple grants OR together — a user's role in this space is the <strong>highest</strong> one whose rule
        matches (design.md §6.4). An "open" space is just a Viewer grant with an Everyone condition.
      </Alert>

      <Stack spacing={2}>
        {rows.map((row) => (
          <Paper key={row.clientId} variant="outlined" sx={{ p: 2 }}>
            <Stack direction="row" spacing={2} sx={{ mb: 1.5, alignItems: 'center' }}>
              <Select
                size="small"
                value={row.role}
                onChange={(e) => setRole(row.clientId, e.target.value as SpaceRole)}
                aria-label="Grant role"
              >
                {(Object.entries(ROLE_LABELS) as [SpaceRole, string][]).map(([value, label]) => (
                  <MenuItem key={value} value={value}>
                    {label}
                  </MenuItem>
                ))}
              </Select>
              <Tooltip title="Remove this grant">
                <IconButton size="small" onClick={() => removeGrant(row.clientId)} aria-label="Remove grant">
                  <DeleteOutlineIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Stack>
            <RuleBuilder
              initialValue={row.initialExpression}
              onChange={(result) => setValidation((prev) => ({ ...prev, [row.clientId]: result }))}
              groups={groups}
              attributes={attributes}
            />
          </Paper>
        ))}
      </Stack>

      <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
        <Button startIcon={<AddIcon />} onClick={addGrant}>
          Add grant
        </Button>
        <Button variant="contained" disabled={!allValid || saving} onClick={() => void handleSave()}>
          Save grants
        </Button>
        {rows.length === 0 && (
          <Typography variant="caption" color="text.secondary">
            No grants — nobody can access this space yet.
          </Typography>
        )}
      </Stack>

      {saveError && <Alert severity="error">{saveError}</Alert>}
      {saved && <Alert severity="success">Saved.</Alert>}
    </Stack>
  )
}

export function SpaceGrantsPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const [{ data, fetching, error }, refetch] = useSpaceGrantsQuery({
    variables: { key: spaceKey ?? '' },
    pause: !spaceKey,
  })
  const [{ data: vocabulary }] = useRuleVocabularyQuery()

  if (!spaceKey) return null

  const groups = vocabulary?.groups ?? []
  const attributes: AttributeOption[] = (vocabulary?.attributeRegistry ?? []).map((definition) => ({
    key: definition.key,
    displayName: definition.displayName ?? undefined,
    allowedValues: definition.allowedValues,
  }))

  const space = data?.space
  // `grants` is the server-computed admin signal: rows come back only to
  // instance/space admins ("absent, not forbidden"), and a space always
  // has at least one grant by construction (creation is atomic with its
  // first grant, design.md §6.5.1) — empty means "not yours to manage".
  const canManage = (space?.grants.length ?? 0) > 0

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Grants: {space?.name ?? spaceKey}
      </Typography>
      <AccessGate allowed={!error && canManage} loading={fetching}>
        {space && (
          <SpaceGrantsEditor
            // Remount the editor after a save-triggered refetch so row
            // baselines (initialExpressionJson etc.) reset to the fresh
            // server state, including server-assigned ids for new rows.
            key={space.grants.map((g) => g.id).join(',')}
            spaceId={space.id}
            initialGrants={space.grants}
            groups={groups}
            attributes={attributes}
            onSaved={() => refetch({ requestPolicy: 'network-only' })}
          />
        )}
      </AccessGate>
    </Stack>
  )
}
