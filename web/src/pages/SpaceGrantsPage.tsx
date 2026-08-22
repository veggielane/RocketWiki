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
import { useAccessRegistryQuery, useSetSpaceGrantsMutation, useSpaceGrantsQuery } from '../graphql/generated/graphql'
import { AccessGate } from '../auth/AccessGate'
import { RuleBuilder } from '../access/RuleBuilder'
import { parseRuleNode, serializeRuleNode } from '../access/ruleSerializer'
import type { ValidationResult } from '../access/builderState'
import type { RuleNode } from '../access/ruleTypes'

const ROLE_LABELS: Record<string, string> = { viewer: 'Viewer', editor: 'Editor', spaceAdmin: 'Space admin' }

interface GrantRow {
  clientId: string
  id: string | null // null = not yet saved
  role: string
  initialExpression: RuleNode
}

interface SpaceGrantsEditorProps {
  spaceKey: string
  initialGrants: { id: string; role: string; expressionJson: string }[]
}

/**
 * design.md §6.4: "multiple grants OR together; your role is the highest
 * whose expression you satisfy" — reuses the same `RuleBuilder` the page
 * restrictions editor does, one per grant, each independently validated
 * before Save is enabled.
 */
function SpaceGrantsEditor({ spaceKey, initialGrants }: SpaceGrantsEditorProps) {
  const [registryQuery] = useAccessRegistryQuery()
  const [, setSpaceGrants] = useSetSpaceGrantsMutation()

  const [rows, setRows] = useState<GrantRow[]>(() =>
    initialGrants.map((g) => ({
      clientId: g.id,
      id: g.id,
      role: g.role,
      initialExpression: parseRuleNode(g.expressionJson),
    })),
  )
  const [validation, setValidation] = useState<Record<string, ValidationResult>>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  const addGrant = () => {
    const clientId = crypto.randomUUID()
    setRows((prev) => [...prev, { clientId, id: null, role: 'viewer', initialExpression: { kind: 'group', group: '' } }])
  }

  const removeGrant = (clientId: string) => {
    setRows((prev) => prev.filter((r) => r.clientId !== clientId))
    setValidation((prev) => {
      const { [clientId]: _removed, ...rest } = prev
      return rest
    })
  }

  const setRole = (clientId: string, role: string) => {
    setRows((prev) => prev.map((r) => (r.clientId === clientId ? { ...r, role } : r)))
  }

  const allValid = rows.length > 0 && rows.every((r) => validation[r.clientId]?.valid)

  const handleSave = async () => {
    if (!allValid) return
    setSaveError(null)
    setSaved(false)
    const result = await setSpaceGrants({
      input: {
        spaceKey,
        grants: rows.map((r) => {
          const v = validation[r.clientId]
          if (!v?.valid) throw new Error('unreachable: allValid already checked')
          return { id: r.id, role: r.role, expressionJson: serializeRuleNode(v.node) }
        }),
      },
    })
    if (result.data?.setSpaceGrants.malformedRuleError) {
      setSaveError(result.data.setSpaceGrants.malformedRuleError.message)
    } else {
      setSaved(true)
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
                onChange={(e) => setRole(row.clientId, e.target.value)}
                aria-label="Grant role"
              >
                {Object.entries(ROLE_LABELS).map(([value, label]) => (
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
              groups={registryQuery.data?.groups ?? []}
              attributes={
                registryQuery.data?.attributeRegistry.map((a) => ({ ...a, displayName: a.displayName ?? undefined })) ??
                []
              }
            />
          </Paper>
        ))}
      </Stack>

      <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
        <Button startIcon={<AddIcon />} onClick={addGrant}>
          Add grant
        </Button>
        <Button variant="contained" disabled={!allValid} onClick={handleSave}>
          Save grants
        </Button>
        {rows.length === 0 && (
          <Typography variant="caption" color="text.secondary">
            No grants — nobody can access this space yet.
          </Typography>
        )}
      </Stack>

      {saveError && <Alert severity="error">{saveError}</Alert>}
      {saved && (
        <Alert severity="success">
          Saved. (This scaffold doesn't refresh rows with server-assigned ids for newly added grants — there's no
          live API to round-trip against yet.)
        </Alert>
      )}
    </Stack>
  )
}

export function SpaceGrantsPage() {
  const { spaceKey } = useParams<{ spaceKey: string }>()
  const [{ data, fetching, error }] = useSpaceGrantsQuery({ variables: { key: spaceKey ?? '' }, pause: !spaceKey })

  if (!spaceKey) return null

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Grants: {data?.space?.name ?? spaceKey}
      </Typography>
      <AccessGate allowed={!error && data?.space?.canManageAccess} loading={fetching}>
        {/* AccessGate only renders children once `allowed` is true, which
            requires `data.space` to exist — safe to assume it here. */}
        {data?.space && <SpaceGrantsEditor spaceKey={spaceKey} initialGrants={data.space.grants} />}
      </AccessGate>
    </Stack>
  )
}
