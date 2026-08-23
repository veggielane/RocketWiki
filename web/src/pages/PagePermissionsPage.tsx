import { useState } from 'react'
import { Link as RouterLink, useParams } from 'react-router-dom'
import {
  Alert,
  Button,
  Chip,
  Divider,
  IconButton,
  Link,
  MenuItem,
  Paper,
  Select,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import {
  useCreateAccessRuleMutation,
  useDeleteAccessRuleMutation,
  usePagePermissionsQuery,
  useRuleVocabularyQuery,
  useUpdateAccessRuleMutation,
  type PageAction,
  type PagePermissionsQuery,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { AccessGate } from '../auth/AccessGate'
import { RuleBuilder } from '../access/RuleBuilder'
import { RuleExpressionOrUnreadable } from '../access/RuleExpressionSummary'
import { PermissionInspectorPanel } from '../access/permission/PermissionInspectorPanel'
import { tryParseRuleNode, serializeRuleNode } from '../access/ruleSerializer'
import type { AttributeOption } from '../access/attributeOption'
import type { ValidationResult } from '../access/builderState'
import type { RuleNode } from '../access/ruleTypes'

const ACTION_LABELS: Record<PageAction, string> = { VIEW: 'View', EDIT: 'Edit' }

type RestrictionDetail = NonNullable<PagePermissionsQuery['page']>['restrictions'][number]

interface RestrictionRow {
  clientId: string
  id: string | null // null = not yet saved
  action: PageAction
  initialAction: PageAction | null
  initialExpressionJson: string | null
  initialExpression: RuleNode
  storedExpressionUnreadable: boolean
}

interface RestrictionsEditorProps {
  pageId: string
  ownRestrictions: RestrictionDetail[]
  groups: string[]
  attributes: AttributeOption[]
  onSaved: () => void
}

/**
 * This page's *own* view/edit restrictions (design.md §6.4), edited with
 * the same `RuleBuilder` the grants pages use. The real API manages rules
 * individually (createAccessRule/updateAccessRule/deleteAccessRule with
 * `kind: PAGE_RESTRICTION` — an append-only audited history, not a set
 * replacement), so Save diffs the rows against what was loaded: added rows
 * are created, edited rows updated, removed rows deleted.
 */
function RestrictionsEditor({ pageId, ownRestrictions, groups, attributes, onSaved }: RestrictionsEditorProps) {
  const [, createAccessRule] = useCreateAccessRuleMutation()
  const [, updateAccessRule] = useUpdateAccessRuleMutation()
  const [, deleteAccessRule] = useDeleteAccessRuleMutation()

  const [rows, setRows] = useState<RestrictionRow[]>(() =>
    ownRestrictions.map((restriction) => {
      const parsed = tryParseRuleNode(restriction.expressionJson)
      return {
        clientId: restriction.ruleId,
        id: restriction.ruleId,
        action: restriction.action,
        initialAction: restriction.action,
        initialExpressionJson: restriction.expressionJson,
        initialExpression: parsed.node ?? { kind: 'group', group: '' },
        storedExpressionUnreadable: parsed.node === null,
      }
    }),
  )
  const [removedRuleIds, setRemovedRuleIds] = useState<string[]>([])
  const [validation, setValidation] = useState<Record<string, ValidationResult>>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [saving, setSaving] = useState(false)

  const addRestriction = () => {
    setRows((prev) => [
      ...prev,
      {
        clientId: crypto.randomUUID(),
        id: null,
        action: 'VIEW',
        initialAction: null,
        initialExpressionJson: null,
        initialExpression: { kind: 'group', group: '' },
        storedExpressionUnreadable: false,
      },
    ])
  }

  const removeRestriction = (clientId: string) => {
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

  const setAction = (clientId: string, action: PageAction) => {
    setRows((prev) => prev.map((r) => (r.clientId === clientId ? { ...r, action } : r)))
  }

  const allValid = rows.every((r) => validation[r.clientId]?.valid)
  const hasWork = rows.length > 0 || removedRuleIds.length > 0

  const handleSave = async () => {
    if (!allValid || !hasWork || saving) return
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
            input: { kind: 'PAGE_RESTRICTION', pageId, action: row.action, expressionJson },
          })
          const errorText = describeMutationError(result.data?.createAccessRule.error)
          if (errorText) {
            setSaveError(errorText)
            return
          }
        } else if (expressionJson !== row.initialExpressionJson || row.action !== row.initialAction) {
          const result = await updateAccessRule({
            input: { accessRuleId: row.id, action: row.action, expressionJson },
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
    <Stack spacing={2}>
      <Stack spacing={2}>
        {rows.map((row) => (
          <Paper key={row.clientId} variant="outlined" sx={{ p: 2 }}>
            <Stack direction="row" spacing={2} sx={{ mb: 1.5, alignItems: 'center' }}>
              <Select
                size="small"
                value={row.action}
                onChange={(e) => setAction(row.clientId, e.target.value as PageAction)}
                aria-label="Restricted action"
              >
                {(Object.entries(ACTION_LABELS) as [PageAction, string][]).map(([value, label]) => (
                  <MenuItem key={value} value={value}>
                    {label}
                  </MenuItem>
                ))}
              </Select>
              <Tooltip title="Remove this restriction">
                <IconButton size="small" onClick={() => removeRestriction(row.clientId)} aria-label="Remove restriction">
                  <DeleteOutlineIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Stack>
            {row.storedExpressionUnreadable && (
              <Alert severity="warning" variant="outlined" sx={{ mb: 1.5 }}>
                This rule's stored expression couldn't be read (it still denies access — malformed rules fail
                closed). Rebuild it below before saving.
              </Alert>
            )}
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
        <Button startIcon={<AddIcon />} onClick={addRestriction}>
          Add restriction
        </Button>
        <Button variant="contained" disabled={!allValid || !hasWork || saving} onClick={() => void handleSave()}>
          Save restrictions
        </Button>
        {rows.length === 0 && (
          <Typography variant="caption" color="text.secondary">
            No restrictions of this page's own — access is governed by space grants and any inherited rules above.
          </Typography>
        )}
      </Stack>

      {saveError && <Alert severity="error">{saveError}</Alert>}
      {saved && <Alert severity="success">Saved.</Alert>}
    </Stack>
  )
}

function InheritedRestrictionList({ restrictions }: { restrictions: RestrictionDetail[] }) {
  if (restrictions.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>
        No restrictions inherited from ancestor pages.
      </Typography>
    )
  }

  return (
    <Stack spacing={1}>
      {restrictions.map((restriction) => (
        <Paper key={restriction.ruleId} variant="outlined" sx={{ p: 1.5 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5, flexWrap: 'wrap' }}>
            <Chip size="small" icon={<LockOutlinedIcon />} label={ACTION_LABELS[restriction.action]} />
            <Typography variant="body2" sx={{ fontWeight: 600 }}>
              Inherited from {restriction.pageTitle}
            </Typography>
            {restriction.updatedByDisplayName && (
              <Typography variant="caption" color="text.secondary">
                last changed by {restriction.updatedByDisplayName}
              </Typography>
            )}
          </Stack>
          <RuleExpressionOrUnreadable node={tryParseRuleNode(restriction.expressionJson).node} />
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
            Restrictions accumulate down the tree — edit this one on{' '}
            <Link component={RouterLink} to={`/pages/${restriction.pageId}/permissions`}>
              {restriction.pageTitle}
            </Link>
            .
          </Typography>
        </Paper>
      ))}
    </Stack>
  )
}

/**
 * design.md §6.6: a page's restriction management plus the permission
 * inspector. `Page.restrictions` lists own + inherited rules (flagged,
 * with source page titles) so an admin sees the whole accumulated chain
 * (§6.4) — inherited rules are shown read-only here and edited at their
 * source page. The whole page is gated on the server-computed
 * `canManageAccess` (instance admin OR this space's space-admin, §6.5.2);
 * self-inspection for ordinary viewers lives on the page view itself.
 */
export function PagePermissionsPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const [{ data, fetching, error }, refetch] = usePagePermissionsQuery({
    variables: { id: pageId ?? '' },
    pause: !pageId,
  })
  // Suggestion vocabulary for the builder's pickers (design.md §6.6) —
  // manage-gated server-side; an error just means typing group names by
  // hand (the picker is freeSolo), never a blocked editor.
  const [{ data: vocabulary }] = useRuleVocabularyQuery()

  if (!pageId) return null

  const page = data?.page
  const groups = vocabulary?.groups ?? []
  const attributes: AttributeOption[] = (vocabulary?.attributeRegistry ?? []).map((definition) => ({
    key: definition.key,
    displayName: definition.displayName ?? undefined,
    allowedValues: definition.allowedValues,
  }))

  const ownRestrictions = page?.restrictions.filter((r) => !r.inherited) ?? []
  const inheritedRestrictions = page?.restrictions.filter((r) => r.inherited) ?? []

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">
        Permissions: {page?.title ?? ''}
      </Typography>
      <AccessGate allowed={!error && page?.canManageAccess} loading={fetching}>
        {page && (
          <Stack spacing={3}>
            <Alert severity="info" variant="outlined">
              Restrictions never widen access — they add conditions on top of the space role, and accumulate down
              the page tree: acting on this page requires satisfying its restrictions <em>and</em> every
              ancestor's (design.md §6.4).
            </Alert>

            <Stack spacing={1.5}>
              <Typography variant="h6" component="h2">
                Inherited restrictions
              </Typography>
              <InheritedRestrictionList restrictions={inheritedRestrictions} />
            </Stack>

            <Stack spacing={1.5}>
              <Typography variant="h6" component="h2">
                This page's restrictions
              </Typography>
              <RestrictionsEditor
                // Remount after a save-triggered refetch so row baselines
                // reset to fresh server state (incl. server-assigned ids).
                key={ownRestrictions.map((r) => r.ruleId).join(',')}
                pageId={page.id}
                ownRestrictions={ownRestrictions}
                groups={groups}
                attributes={attributes}
                onSaved={() => refetch({ requestPolicy: 'network-only' })}
              />
            </Stack>

            <Divider />

            <Stack spacing={1.5}>
              <Typography variant="h6" component="h2">
                Permission inspector
              </Typography>
              <PermissionInspectorPanel pageId={page.id} allowSubjectInput />
            </Stack>
          </Stack>
        )}
      </AccessGate>
    </Stack>
  )
}
