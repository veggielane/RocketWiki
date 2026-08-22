import { useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { Alert, Button, Divider, Stack, TextField, Typography } from '@mui/material'
import { usePagePermissionsQuery, useSetPageRestrictionsMutation, useAccessRegistryQuery, useCurrentUserQuery } from '../graphql/generated/graphql'
import { AccessGate } from '../auth/AccessGate'
import { PermissionInspector } from '../access/permission/PermissionInspector'
import type { EffectivePermissionDetail } from '../access/permission/effectivePermissionTypes'
import { RuleBuilder } from '../access/RuleBuilder'
import { parseRuleNode, serializeRuleNode } from '../access/ruleSerializer'
import { everyone, type RuleNode } from '../access/ruleTypes'
import type { ValidationResult } from '../access/builderState'

/**
 * design.md §6.6: the permission inspector ("why can/can't user X see this
 * page") plus editing the page's own view/edit restrictions with the rule
 * builder. Gated on `page.canManageAccess` (server-computed: instance admin
 * OR this page's space-admin) via `AccessGate` — an unauthorized visitor
 * sees the same not-found page as a nonexistent pageId (design.md §6.7),
 * not a "forbidden" screen confirming the page is there.
 */
export function PagePermissionsPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const [{ data: meData }] = useCurrentUserQuery()
  const [inspectedUserId, setInspectedUserId] = useState('')
  const effectiveUserId = inspectedUserId || meData?.me.id || ''

  const [{ data, fetching, error }] = usePagePermissionsQuery({
    variables: { pageId: pageId ?? '', userId: effectiveUserId },
    pause: !pageId || !effectiveUserId,
  })
  const [{ data: registryData }] = useAccessRegistryQuery()
  const [, setPageRestrictions] = useSetPageRestrictionsMutation()

  const [viewPending, setViewPending] = useState<ValidationResult | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  const detail: EffectivePermissionDetail | null = useMemo(() => {
    const raw = data?.page?.effectivePermissionDetail
    if (!raw) return null
    const toCheck = (c: (typeof raw.viewRestrictions)[number]) => ({
      ruleId: c.ruleId,
      pageId: c.pageId,
      pageTitle: c.pageTitle,
      action: c.action as 'view' | 'edit',
      expression: parseRuleNode(c.expressionJson),
      passed: c.passed,
    })
    return {
      userId: raw.userId,
      userDisplayName: raw.userDisplayName,
      spaceRole: (raw.spaceRole as EffectivePermissionDetail['spaceRole']) ?? null,
      isReplicaSpace: raw.isReplicaSpace,
      canView: raw.canView,
      canEdit: raw.canEdit,
      viewDenialReason: raw.viewDenialReason ?? null,
      editDenialReason: raw.editDenialReason ?? null,
      viewRestrictions: raw.viewRestrictions.map(toCheck),
      editRestrictions: raw.editRestrictions.map(toCheck),
    }
  }, [data])

  const currentViewRule: RuleNode = useMemo(() => {
    const json = data?.page?.restrictions.viewExpressionJson
    return json ? parseRuleNode(json) : everyone()
  }, [data])

  const handleSaveViewRestriction = async () => {
    if (!pageId || !viewPending?.valid) return
    setSaveError(null)
    setSaved(false)
    const result = await setPageRestrictions({
      input: { pageId, viewExpressionJson: serializeRuleNode(viewPending.node) },
    })
    if (result.data?.setPageRestrictions.malformedRuleError) {
      setSaveError(result.data.setPageRestrictions.malformedRuleError.message)
    } else {
      setSaved(true)
    }
  }

  if (!pageId) return null

  return (
    <AccessGate allowed={!error && data?.page?.canManageAccess} loading={fetching}>
      {!detail ? (
        <Alert severity="info">Couldn't load permissions — there's no live API in this environment yet.</Alert>
      ) : (
        <Stack spacing={4}>
          <Stack spacing={2}>
            <Typography variant="h4" component="h1">
              Permissions: {data?.page?.title}
            </Typography>
            <TextField
              label="Inspect as user ID"
              value={inspectedUserId}
              onChange={(e) => setInspectedUserId(e.target.value)}
              placeholder={meData?.me.id ?? 'user id'}
              helperText="Defaults to you. No user directory yet — enter a subject id to inspect someone else."
              size="small"
              sx={{ maxWidth: 320 }}
            />
            <PermissionInspector detail={detail} />
          </Stack>

          <Divider />

          <Stack spacing={2}>
            <Typography variant="h5">Edit view restriction</Typography>
            <Typography variant="body2" color="text.secondary">
              This page's own restriction — inherited ancestor restrictions (shown above) always apply on top of it
              and can't be edited from here (design.md §6.4).
            </Typography>
            <RuleBuilder
              initialValue={currentViewRule}
              onChange={setViewPending}
              groups={registryData?.groups ?? []}
              attributes={
                registryData?.attributeRegistry.map((a) => ({ ...a, displayName: a.displayName ?? undefined })) ?? []
              }
            />
            {saveError && <Alert severity="error">{saveError}</Alert>}
            {saved && <Alert severity="success">Saved.</Alert>}
            <Button
              variant="contained"
              disabled={!viewPending?.valid}
              onClick={handleSaveViewRestriction}
              sx={{ alignSelf: 'flex-start' }}
            >
              Save restriction
            </Button>
          </Stack>
        </Stack>
      )}
    </AccessGate>
  )
}
