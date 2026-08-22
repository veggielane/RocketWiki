import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Button, MenuItem, Paper, Select, Stack, TextField, Typography } from '@mui/material'
import { useAccessRegistryQuery, useCreateSpaceMutation } from '../graphql/generated/graphql'
import { RuleBuilder } from '../access/RuleBuilder'
import { serializeRuleNode } from '../access/ruleSerializer'
import type { ValidationResult } from '../access/builderState'

const ROLE_LABELS: Record<string, string> = { viewer: 'Viewer', editor: 'Editor', spaceAdmin: 'Space admin' }

/**
 * design.md §6.5.1: space creation is atomic with its first grant — a
 * space with zero grants would be permanently unadministerable, since
 * granting access itself requires being that space's admin already. The
 * initial grant is a required input with **no default**: this form cannot
 * be submitted without one, and deliberately never offers "skip" or
 * pre-fills `everyone` — silently opening a new space to everyone is
 * exactly the mistake ABAC exists to prevent. The `RuleBuilder` below
 * starts on a blank, incomplete condition (same as "add grant" elsewhere),
 * not a valid-but-dangerous default.
 */
export function CreateSpacePage() {
  const navigate = useNavigate()
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [role, setRole] = useState('')
  const [grantValidation, setGrantValidation] = useState<ValidationResult | null>(null)
  const [keyError, setKeyError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const [{ data: registryData }] = useAccessRegistryQuery()
  const [, createSpace] = useCreateSpaceMutation()

  const canSubmit = key.trim().length > 0 && name.trim().length > 0 && role !== '' && grantValidation?.valid === true

  const handleSubmit = async () => {
    if (!canSubmit || !grantValidation?.valid) return
    setCreating(true)
    setKeyError(null)
    const result = await createSpace({
      input: {
        key: key.trim(),
        name: name.trim(),
        description: description.trim() || null,
        initialGrant: { id: null, role, expressionJson: serializeRuleNode(grantValidation.node) },
      },
    })
    setCreating(false)
    const payload = result.data?.createSpace
    if (payload?.keyAlreadyInUseError) {
      setKeyError(payload.keyAlreadyInUseError.message)
      return
    }
    if (payload?.space) {
      navigate(`/spaces/${payload.space.key}`)
    }
  }

  return (
    <Stack spacing={3} sx={{ maxWidth: 640 }}>
      <Typography variant="h4" component="h1">
        New space
      </Typography>

      <TextField
        label="Key"
        value={key}
        onChange={(e) => setKey(e.target.value)}
        error={Boolean(keyError)}
        helperText={keyError ?? 'Short slug, e.g. "ENG" — used in URLs, cannot be changed later.'}
        size="small"
      />
      <TextField label="Name" value={name} onChange={(e) => setName(e.target.value)} size="small" />
      <TextField
        label="Description"
        value={description}
        onChange={(e) => setDescription(e.target.value)}
        size="small"
        multiline
        minRows={2}
      />

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="h6" gutterBottom>
          Initial grant
        </Typography>
        <Alert severity="info" variant="outlined" sx={{ mb: 2 }}>
          Every space needs at least one grant so someone can administer it — a space created with none would be
          permanently locked, since managing grants itself requires being this space's admin. There's no default
          like "everyone": choose who should have access from the start.
        </Alert>

        <Stack spacing={2}>
          <Select
            size="small"
            displayEmpty
            value={role}
            onChange={(e) => setRole(e.target.value)}
            sx={{ maxWidth: 220 }}
            aria-label="Initial grant role"
          >
            <MenuItem value="" disabled>
              Choose a role…
            </MenuItem>
            {Object.entries(ROLE_LABELS).map(([value, label]) => (
              <MenuItem key={value} value={value}>
                {label}
              </MenuItem>
            ))}
          </Select>

          <RuleBuilder
            initialValue={{ kind: 'group', group: '' }}
            onChange={setGrantValidation}
            groups={registryData?.groups ?? []}
            attributes={
              registryData?.attributeRegistry.map((a) => ({ ...a, displayName: a.displayName ?? undefined })) ?? []
            }
          />
        </Stack>
      </Paper>

      <Button variant="contained" disabled={!canSubmit || creating} onClick={() => void handleSubmit()} sx={{ alignSelf: 'flex-start' }}>
        Create space
      </Button>
    </Stack>
  )
}
