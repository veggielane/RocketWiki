import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Button, MenuItem, Paper, Select, Stack, TextField, Typography } from '@mui/material'
import { useCreateSpaceMutation, useRuleVocabularyQuery, type SpaceRole } from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { describeWriteFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { RuleBuilder } from '../access/RuleBuilder'
import { serializeRuleNode } from '../access/ruleSerializer'
import type { AttributeOption } from '../access/attributeOption'
import type { ValidationResult } from '../access/builderState'

const ROLE_LABELS: Record<SpaceRole, string> = { VIEWER: 'Viewer', EDITOR: 'Editor', SPACE_ADMIN: 'Space admin' }

/**
 * design.md §6.5.1: space creation is atomic with its first grant — a
 * space with zero grants would be permanently unadministerable, since
 * granting access itself requires being that space's admin already. The
 * real mutation takes `initialGrant` as a separate **required** argument
 * with no default: this form cannot be submitted without one, and
 * deliberately never offers "skip" or pre-fills `everyone` — silently
 * opening a new space to everyone is exactly the mistake ABAC exists to
 * prevent. The `RuleBuilder` below starts on a blank, incomplete condition
 * (same as "add grant" elsewhere), not a valid-but-dangerous default.
 *
 * A duplicate key comes back as the flattened error with kind
 * "Validation"; it's surfaced on the key field rather than as a toast.
 */
export function CreateSpacePage() {
  useDocumentTitle('New space')
  const navigate = useNavigate()
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [role, setRole] = useState<SpaceRole | ''>('')
  const [grantValidation, setGrantValidation] = useState<ValidationResult | null>(null)
  const [keyError, setKeyError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const [, createSpace] = useCreateSpaceMutation()
  // Suggestion vocabulary for the builder's pickers (design.md §6.6) —
  // instance admins (the only ones who reach this page) are always rule
  // managers, so the manage-gated query answers; suggestions, never
  // authority (the group picker stays freeSolo).
  const [{ data: vocabulary }] = useRuleVocabularyQuery()
  const vocabularyGroups = vocabulary?.groups ?? []
  const vocabularyAttributes: AttributeOption[] = (vocabulary?.attributeRegistry ?? []).map((definition) => ({
    key: definition.key,
    displayName: definition.displayName ?? undefined,
    allowedValues: definition.allowedValues,
  }))

  const canSubmit = key.trim().length > 0 && name.trim().length > 0 && role !== '' && grantValidation?.valid === true

  const handleSubmit = async () => {
    // `canSubmit` already narrows `role` to a SpaceRole (aliased-condition
    // narrowing) and re-checks grant validity.
    if (!canSubmit || !grantValidation?.valid) return
    setCreating(true)
    setKeyError(null)
    const result = await createSpace({
      input: {
        key: key.trim(),
        name: name.trim(),
        description: description.trim() || null,
      },
      initialGrant: { role, expressionJson: serializeRuleNode(grantValidation.node) },
    })
    setCreating(false)
    // A transport failure has no `data` at all, so `describeMutationError`
    // returns null and nothing was said: the button un-busied, no navigation
    // happened, and pressing Create visibly did nothing. Checked before the
    // typed refusals, which are a different (and successful) round trip.
    if (result.error !== undefined) {
      setKeyError(describeWriteFailure('SPACE').summary)
      return
    }
    const payload = result.data?.createSpace
    const errorText = describeMutationError(payload?.error)
    if (errorText) {
      setKeyError(errorText)
      return
    }
    if (payload?.space) {
      navigate(`/spaces/${payload.space.key}`)
    }
  }

  return (
    <Stack spacing={3} sx={{ maxWidth: 640 }}>
      <PageHeader title="New space" subject={{ label: 'Spaces', to: '/' }} />

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
            onChange={(e) => setRole(e.target.value as SpaceRole | '')}
            sx={{ maxWidth: 220 }}
            aria-label="Initial grant role"
          >
            <MenuItem value="" disabled>
              Choose a role…
            </MenuItem>
            {(Object.entries(ROLE_LABELS) as [SpaceRole, string][]).map(([value, label]) => (
              <MenuItem key={value} value={value}>
                {label}
              </MenuItem>
            ))}
          </Select>

          <RuleBuilder
            initialValue={{ kind: 'group', group: '' }}
            onChange={setGrantValidation}
            groups={vocabularyGroups}
            attributes={vocabularyAttributes}
          />
        </Stack>
      </Paper>

      <Button variant="contained" disabled={!canSubmit || creating} onClick={() => void handleSubmit()} sx={{ alignSelf: 'flex-start' }}>
        Create space
      </Button>
    </Stack>
  )
}
