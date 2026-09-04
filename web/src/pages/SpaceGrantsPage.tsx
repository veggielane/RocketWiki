import { useState, type ReactNode } from 'react'
import { useParams } from 'react-router-dom'
import {
  Alert,
  Button,
  IconButton,
  MenuItem,
  Paper,
  Select,
  Snackbar,
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
  useSelectorCategoriesQuery,
  useSpaceGrantsQuery,
  useUpdateAccessRuleMutation,
  type CreateAccessRuleRequestInput,
  type SpaceGrantsQuery,
  type SpaceRole,
  type UpdateAccessRuleRequestInput,
} from '../graphql/generated/graphql'
import { describeMutationError } from '../graphql/mutationError'
import { describeWriteFailure, type WriteFailureReason } from '../feedback/unavailableCopy'
import { SNACKBAR_AUTO_HIDE_MS } from '../feedback/snackbar'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { AccessGate } from '../auth/AccessGate'
import { RuleBuilder } from '../access/RuleBuilder'
import { SelectorValuePicker, type SelectorCategoryOption } from '../access/SelectorValuePicker'
import { GRANT_KINDS_EXPLANATION } from '../access/denial/protectedCopy'
import { parseRuleNode, serializeRuleNode } from '../access/ruleSerializer'
import type { AttributeOption } from '../access/attributeOption'
import type { ValidationResult } from '../access/builderState'
import type { RuleNode } from '../access/ruleTypes'
import type { SelectorValue } from '../markings/markingRefusal'

const ROLE_LABELS: Record<SpaceRole, string> = { EDITOR: 'Editor', SPACE_ADMIN: 'Space admin' }

type Grant = NonNullable<SpaceGrantsQuery['space']>['grants'][number]

interface RowBase {
  clientId: string
  id: string | null // null = not yet saved
  initialExpressionJson: string | null
  initialExpression: RuleNode
}

/** An ACCESS grant row: who may see the space, and which selector values they are granted here. */
interface AccessRow extends RowBase {
  selectorValues: SelectorValue[]
  initialSelectorKey: string | null
}

/** A ROLE grant row: who may edit or administer. Confers no visibility. */
interface RoleRow extends RowBase {
  role: SpaceRole
  initialRole: SpaceRole | null
}

function selectorKey(values: readonly SelectorValue[]): string {
  return values
    .map((v) => `${v.category}=${v.value}`)
    .sort()
    .join('\0')
}

interface GrantSectionProps<Row extends RowBase> {
  title: string
  description: string
  addLabel: string
  saveLabel: string
  savedNotice: string
  emptyNote: string
  writeReason: WriteFailureReason
  initialRows: Row[]
  newRow: () => Row
  /** The row's controls beside the remove button — a role select, or a selector picker. */
  renderExtras: (row: Row, update: (updater: (row: Row) => Row) => void) => ReactNode
  createInput: (row: Row, expressionJson: string) => CreateAccessRuleRequestInput
  updateInput: (row: Row, expressionJson: string) => UpdateAccessRuleRequestInput
  /** Whether a saved row differs from what was loaded — the diff-on-save test for an update. */
  changed: (row: Row, expressionJson: string) => boolean
  groups: string[]
  attributes: AttributeOption[]
  onSaved: () => void
}

/**
 * One kind of grant, edited as a set and saved as a diff. The real API
 * manages rules individually (createAccessRule/updateAccessRule/
 * deleteAccessRule — an append-only audited history, not a set replacement),
 * so Save diffs the rows against what was loaded: added rows are created,
 * changed rows updated, removed rows deleted. Each row is the same
 * `RuleBuilder` the create-space form uses, independently validated before
 * Save is enabled.
 *
 * Picker vocabulary (known groups + the attribute registry) comes from the
 * manage-gated `RuleVocabulary` query — suggestions, never authority: the
 * group picker stays freeSolo, so an empty or failed vocabulary query just
 * means typing by hand (design.md §6.6).
 */
function GrantSection<Row extends RowBase>({
  title,
  description,
  addLabel,
  saveLabel,
  savedNotice,
  emptyNote,
  writeReason,
  initialRows,
  newRow,
  renderExtras,
  createInput,
  updateInput,
  changed,
  groups,
  attributes,
  onSaved,
}: GrantSectionProps<Row>) {
  const [, createAccessRule] = useCreateAccessRuleMutation()
  const [, updateAccessRule] = useUpdateAccessRuleMutation()
  const [, deleteAccessRule] = useDeleteAccessRuleMutation()

  const [rows, setRows] = useState<Row[]>(initialRows)
  const [removedRuleIds, setRemovedRuleIds] = useState<string[]>([])
  const [validation, setValidation] = useState<Record<string, ValidationResult>>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [saving, setSaving] = useState(false)

  const addRow = () => setRows((prev) => [...prev, newRow()])

  const removeRow = (clientId: string) => {
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

  const updateRow = (clientId: string, updater: (row: Row) => Row) =>
    setRows((prev) => prev.map((r) => (r.clientId === clientId ? updater(r) : r)))

  const allValid = rows.every((r) => validation[r.clientId]?.valid)
  // Nothing to save when nothing changed: no new rows, no removals, and every
  // saved row still matches what was loaded.
  const anythingToSave =
    removedRuleIds.length > 0 ||
    rows.some((r) => {
      const v = validation[r.clientId]
      return r.id === null || (v?.valid === true && changed(r, serializeRuleNode(v.node)))
    })

  const handleSave = async () => {
    if (!allValid || !anythingToSave || saving) return
    setSaveError(null)
    setSaved(false)
    setSaving(true)
    try {
      for (const row of rows) {
        const v = validation[row.clientId]
        if (!v?.valid) throw new Error('unreachable: allValid already checked')
        const expressionJson = serializeRuleNode(v.node)

        if (row.id === null) {
          const result = await createAccessRule({ input: createInput(row, expressionJson) })
          if (result.error !== undefined) {
            setSaveError(describeWriteFailure(writeReason).summary)
            return
          }
          const errorText = describeMutationError(result.data?.createAccessRule.error)
          if (errorText) {
            setSaveError(errorText)
            return
          }
        } else if (changed(row, expressionJson)) {
          const result = await updateAccessRule({ input: updateInput(row, expressionJson) })
          if (result.error !== undefined) {
            setSaveError(describeWriteFailure(writeReason).summary)
            return
          }
          const errorText = describeMutationError(result.data?.updateAccessRule.error)
          if (errorText) {
            setSaveError(errorText)
            return
          }
        }
      }

      for (const ruleId of removedRuleIds) {
        const result = await deleteAccessRule({ input: { accessRuleId: ruleId } })
        if (result.error !== undefined) {
          setSaveError(describeWriteFailure(writeReason).summary)
          return
        }
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
    <Paper component="section" variant="outlined" sx={{ p: 2 }} aria-labelledby={`grants-${writeReason}`}>
      <Stack spacing={2}>
        <Stack spacing={0.5}>
          <Typography variant="h6" component="h2" id={`grants-${writeReason}`}>
            {title}
          </Typography>
          <Typography variant="body2" color="text.secondary">
            {description}
          </Typography>
        </Stack>

        <Stack spacing={2}>
          {rows.map((row) => (
            <Paper key={row.clientId} variant="outlined" sx={{ p: 2 }}>
              <Stack direction="row" spacing={2} sx={{ mb: 1.5, alignItems: 'flex-start' }}>
                {renderExtras(row, (updater) => updateRow(row.clientId, updater))}
                <Tooltip title="Remove this grant">
                  <IconButton size="small" onClick={() => removeRow(row.clientId)} aria-label="Remove grant">
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

        <Stack direction="row" spacing={2} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Button startIcon={<AddIcon />} onClick={addRow}>
            {addLabel}
          </Button>
          <Button variant="contained" disabled={!allValid || !anythingToSave || saving} onClick={() => void handleSave()}>
            {saveLabel}
          </Button>
          {rows.length === 0 && (
            <Typography variant="caption" color="text.secondary">
              {emptyNote}
            </Typography>
          )}
        </Stack>

        {saveError && (
          <Alert severity="warning" onClose={() => setSaveError(null)}>
            {saveError}
          </Alert>
        )}
        {/* Snackbar, not a permanent banner — see PagePermissionsPage. */}
        <Snackbar open={saved} autoHideDuration={SNACKBAR_AUTO_HIDE_MS} onClose={() => setSaved(false)}>
          <Alert severity="success" onClose={() => setSaved(false)}>
            {savedNotice}
          </Alert>
        </Snackbar>
      </Stack>
    </Paper>
  )
}

interface SpaceGrantsEditorProps {
  spaceId: string
  grants: Grant[]
  groups: string[]
  attributes: AttributeOption[]
  categories: SelectorCategoryOption[]
  onSaved: () => void
}

/**
 * design.md §6.4: two kinds of grant on a space, two sections. **Access**
 * grants say who may SEE the space's pages and which selector values they
 * are granted here (§21.15) — several values per category are allowed on a
 * grant, and a reader's granted set is the union over every access grant
 * they match. **Role** grants say who may EDIT (Editor) or ADMINISTER (Space
 * admin), and confer no visibility of their own: a space admin with no access
 * grant manages a space whose every page reads as protected to them. There
 * is no viewer role — viewing is holding a matching access grant.
 */
function SpaceGrantsEditor({ spaceId, grants, groups, attributes, categories, onSaved }: SpaceGrantsEditorProps) {
  const accessRows: AccessRow[] = grants
    .filter((g) => g.kind === 'ACCESS_GRANT')
    .map((g) => ({
      clientId: g.id,
      id: g.id,
      initialExpressionJson: g.expressionJson,
      initialExpression: parseRuleNode(g.expressionJson),
      selectorValues: g.selectorValues.map(({ category, value }) => ({ category, value })),
      initialSelectorKey: selectorKey(g.selectorValues),
    }))
  const roleRows: RoleRow[] = grants
    .filter((g) => g.kind === 'ROLE_GRANT')
    .map((g) => ({
      clientId: g.id,
      id: g.id,
      initialExpressionJson: g.expressionJson,
      initialExpression: parseRuleNode(g.expressionJson),
      // A role grant always carries a role (the server's check constraint);
      // Editor is the conservative reading of a row that somehow does not.
      role: g.role ?? 'EDITOR',
      initialRole: g.role,
    }))

  return (
    <Stack spacing={3}>
      <Alert severity="info" variant="outlined">
        {GRANT_KINDS_EXPLANATION}
      </Alert>

      <GrantSection<AccessRow>
        title="Access"
        description="Who may see this space's pages, and which selector values they are granted here. A reader's granted values are the union across every access grant that matches them."
        addLabel="Add access grant"
        saveLabel="Save access grants"
        savedNotice="Access grants saved."
        emptyNote="No access grants — nobody can see this space's pages yet."
        writeReason="ACCESS_GRANT"
        initialRows={accessRows}
        newRow={() => ({
          clientId: crypto.randomUUID(),
          id: null,
          initialExpressionJson: null,
          initialExpression: { kind: 'group', group: '' },
          selectorValues: [],
          initialSelectorKey: null,
        })}
        renderExtras={(row, update) => (
          <SelectorValuePicker
            categories={categories}
            value={row.selectorValues}
            onChange={(next) => update((r) => ({ ...r, selectorValues: next }))}
          />
        )}
        createInput={(row, expressionJson) => ({
          kind: 'ACCESS_GRANT',
          spaceId,
          expressionJson,
          selectorValues: row.selectorValues,
        })}
        updateInput={(row, expressionJson) => ({
          accessRuleId: row.id!,
          expressionJson,
          selectorValues: row.selectorValues,
        })}
        changed={(row, expressionJson) =>
          expressionJson !== row.initialExpressionJson || selectorKey(row.selectorValues) !== row.initialSelectorKey
        }
        groups={groups}
        attributes={attributes}
        onSaved={onSaved}
      />

      <GrantSection<RoleRow>
        title="Roles"
        description="Who may edit or administer this space. A role confers no visibility: someone with a role and no access grant manages pages they cannot read."
        addLabel="Add role grant"
        saveLabel="Save role grants"
        savedNotice="Role grants saved."
        emptyNote="No role grants — nobody but an instance admin can edit or administer this space yet."
        writeReason="ROLE_GRANT"
        initialRows={roleRows}
        newRow={() => ({
          clientId: crypto.randomUUID(),
          id: null,
          initialExpressionJson: null,
          initialExpression: { kind: 'group', group: '' },
          role: 'EDITOR',
          initialRole: null,
        })}
        renderExtras={(row, update) => (
          <Select
            size="small"
            value={row.role}
            onChange={(e) => update((r) => ({ ...r, role: e.target.value as SpaceRole }))}
            aria-label="Grant role"
          >
            {(Object.entries(ROLE_LABELS) as [SpaceRole, string][]).map(([value, label]) => (
              <MenuItem key={value} value={value}>
                {label}
              </MenuItem>
            ))}
          </Select>
        )}
        createInput={(row, expressionJson) => ({ kind: 'ROLE_GRANT', spaceId, role: row.role, expressionJson })}
        updateInput={(row, expressionJson) => ({ accessRuleId: row.id!, role: row.role, expressionJson })}
        changed={(row, expressionJson) => expressionJson !== row.initialExpressionJson || row.role !== row.initialRole}
        groups={groups}
        attributes={attributes}
        onSaved={onSaved}
      />
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
  // §21.15: the selector vocabulary an access grant may confer. Instance
  // configuration, read rather than typed.
  const [{ data: categoriesData }] = useSelectorCategoriesQuery()
  useDocumentTitle(data?.space ? `Grants — ${data.space.name}` : 'Grants')

  if (!spaceKey) return null

  const groups = vocabulary?.groups ?? []
  const attributes: AttributeOption[] = (vocabulary?.attributeRegistry ?? []).map((definition) => ({
    key: definition.key,
    displayName: definition.displayName ?? undefined,
    allowedValues: definition.allowedValues,
  }))
  const categories: SelectorCategoryOption[] = categoriesData?.selectorCategories ?? []

  const space = data?.space
  // The server's own answer (space-admin role grant, or instance admin) —
  // never inferred from the grant list, which is empty for a manager on a
  // space with no grants and which now includes rows that say nothing about
  // who may manage anything.
  const canManage = space?.canManageAccess === true

  return (
    <Stack spacing={2}>
      {/* Inside the gate — a heading naming the space above a 404 is a screen
          contradicting itself. Same fix as PagePermissionsPage. */}
      <AccessGate allowed={!error && canManage} loading={fetching}>
        {space && (
          <Stack spacing={2}>
            <PageHeader title="Grants" subject={{ label: space.name, to: `/spaces/${space.key}` }} />
            <SpaceGrantsEditor
              // Remount the editor after a save-triggered refetch so row
              // baselines (initialExpressionJson etc.) reset to the fresh
              // server state, including server-assigned ids for new rows.
              key={space.grants.map((g) => g.id).join(',')}
              spaceId={space.id}
              grants={space.grants}
              groups={groups}
              attributes={attributes}
              categories={categories}
              onSaved={() => refetch({ requestPolicy: 'network-only' })}
            />
          </Stack>
        )}
      </AccessGate>
    </Stack>
  )
}
