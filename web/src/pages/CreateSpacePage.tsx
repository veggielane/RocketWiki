import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Button, IconButton, Paper, Stack, TextField, Tooltip, Typography } from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import {
  useCreateSpaceMutation,
  useCurrentUserQuery,
  useRuleVocabularyQuery,
  useSelectorCategoriesQuery,
  type InitialGrantInput,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { describeWriteFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { RuleBuilder } from '../access/RuleBuilder'
import { SelectorValuePicker, type SelectorCategoryOption } from '../access/SelectorValuePicker'
import { serializeRuleNode } from '../access/ruleSerializer'
import { group, user, type RuleNode } from '../access/ruleTypes'
import type { AttributeOption } from '../access/attributeOption'
import type { ValidationResult } from '../access/builderState'
import type { SelectorValue } from '../markings/clearance'

/**
 * design.md §6.5.1: space creation is atomic with its first grants — a
 * space with no administrator would be permanently unadministerable (short
 * of an instance admin), since granting access itself requires being that
 * space's admin already. The real mutation takes `initialGrants` as a
 * separate **required** list and refuses one without a Space admin role
 * grant; this form pre-fills that grant with the creator's own user, which
 * is the one subject it can be sure of.
 *
 * Access is the other kind of grant (§6.4) and is OPTIONAL here: a new space
 * is visible to nobody until an access grant says otherwise. The form
 * deliberately never offers "skip" for the administrator and never pre-fills
 * `everyone` for access — silently opening a new space to everyone is exactly
 * the mistake ABAC exists to prevent. The access `RuleBuilder`, when added,
 * starts on a blank, incomplete condition, not a valid-but-dangerous default.
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
  const [adminValidation, setAdminValidation] = useState<ValidationResult | null>(null)
  const [accessValidation, setAccessValidation] = useState<ValidationResult | null>(null)
  const [accessGrant, setAccessGrant] = useState<{ selectorValues: SelectorValue[] } | null>(null)
  const [keyError, setKeyError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const [, createSpace] = useCreateSpaceMutation()
  // The creator's own token subject, for the pre-filled administrator grant.
  const [{ data: meData }] = useCurrentUserQuery()
  // Suggestion vocabulary for the builder's pickers (design.md §6.6) —
  // instance admins (the only ones who reach this page) are always rule
  // managers, so the manage-gated query answers; suggestions, never
  // authority (the group picker stays freeSolo).
  const [{ data: vocabulary }] = useRuleVocabularyQuery()
  const [{ data: categoriesData }] = useSelectorCategoriesQuery()
  const vocabularyGroups = vocabulary?.groups ?? []
  const vocabularyAttributes: AttributeOption[] = (vocabulary?.attributeRegistry ?? []).map((definition) => ({
    key: definition.key,
    displayName: definition.displayName ?? undefined,
    allowedValues: definition.allowedValues,
  }))
  const categories: SelectorCategoryOption[] = categoriesData?.selectorCategories ?? []

  // `RuleBuilder` seeds itself once on mount, so the administrator builder is
  // keyed on the creator's id: it mounts on a blank condition until `me`
  // answers, then remounts pre-filled with the one subject the form can be
  // sure of.
  const creatorId = meData?.me.id ?? null
  const adminSeed: RuleNode = creatorId ? user(creatorId) : group('')

  const accessValid = accessGrant === null || accessValidation?.valid === true
  const canSubmit = key.trim().length > 0 && name.trim().length > 0 && adminValidation?.valid === true && accessValid

  const handleSubmit = async () => {
    if (!canSubmit || !adminValidation?.valid) return
    const initialGrants: InitialGrantInput[] = [
      { kind: 'ROLE_GRANT', role: 'SPACE_ADMIN', expressionJson: serializeRuleNode(adminValidation.node) },
    ]
    if (accessGrant !== null && accessValidation?.valid) {
      initialGrants.push({
        kind: 'ACCESS_GRANT',
        expressionJson: serializeRuleNode(accessValidation.node),
        selectorValues: accessGrant.selectorValues,
      })
    }
    setCreating(true)
    setKeyError(null)
    const result = await createSpace({
      input: {
        key: key.trim(),
        name: name.trim(),
        description: description.trim() || null,
      },
      initialGrants,
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
        <Typography variant="h6" component="h2" gutterBottom>
          Who administers this space
        </Typography>
        <Alert severity="info" variant="outlined" sx={{ mb: 2 }}>
          Every space needs at least one administrator, so a Space admin role grant for you is filled in. A
          role confers no visibility: administering the space and reading its pages are separate grants.
        </Alert>
        <RuleBuilder
          key={creatorId ?? 'anonymous'}
          initialValue={adminSeed}
          onChange={setAdminValidation}
          groups={vocabularyGroups}
          attributes={vocabularyAttributes}
        />
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="h6" component="h2" gutterBottom>
          Who can see it
        </Typography>
        <Alert severity="info" variant="outlined" sx={{ mb: 2 }}>
          Optional. Until an access grant names them, nobody can see this space's pages — there is no default
          like "everyone". Grants can be added at any time from the space's settings.
        </Alert>
        {accessGrant === null ? (
          <Button startIcon={<AddIcon />} onClick={() => setAccessGrant({ selectorValues: [] })}>
            Add access grant
          </Button>
        ) : (
          <Stack spacing={2}>
            <Stack direction="row" spacing={2} sx={{ alignItems: 'flex-start' }}>
              <SelectorValuePicker
                categories={categories}
                value={accessGrant.selectorValues}
                onChange={(next) => setAccessGrant({ selectorValues: next })}
              />
              <Tooltip title="Remove this grant">
                <IconButton
                  size="small"
                  aria-label="Remove access grant"
                  onClick={() => {
                    setAccessGrant(null)
                    setAccessValidation(null)
                  }}
                >
                  <DeleteOutlineIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Stack>
            <RuleBuilder
              initialValue={{ kind: 'group', group: '' }}
              onChange={setAccessValidation}
              groups={vocabularyGroups}
              attributes={vocabularyAttributes}
            />
          </Stack>
        )}
      </Paper>

      <Button variant="contained" disabled={!canSubmit || creating} onClick={() => void handleSubmit()} sx={{ alignSelf: 'flex-start' }}>
        Create space
      </Button>
    </Stack>
  )
}
