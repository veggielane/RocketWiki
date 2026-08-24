import { Alert, Box, Chip, Paper, Stack, Typography } from '@mui/material'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutlined'
import CancelOutlinedIcon from '@mui/icons-material/CancelOutlined'
import { RuleExpressionOrUnreadable } from '../RuleExpressionSummary'
import { describeDenialReason } from './describeDenialReason'
import type { EffectivePermissionDetail, RestrictionCheck } from './effectivePermissionTypes'

const SPACE_ROLE_LABELS: Record<string, string> = {
  viewer: 'Viewer',
  editor: 'Editor',
  spaceAdmin: 'Space admin',
}

/**
 * design.md §6.6's permission inspector: "why can/can't user X see this
 * page" — the space-role computation and every restriction with pass/fail
 * per condition. Read-only; there is nothing here to edit (rule editing is
 * `RuleBuilder`'s job).
 */
export function PermissionInspector({ detail }: { detail: EffectivePermissionDetail }) {
  return (
    <Stack spacing={2}>
      <Typography variant="h6">
        Why can {detail.userDisplayName} {detail.canView ? '' : "n't"} see this page?
      </Typography>

      {detail.isReplicaSpace && (
        <Alert severity="info">
          This space is a replica of another instance — it's read-only regardless of any grant (design.md §12).
        </Alert>
      )}

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="subtitle2" color="text.secondary" gutterBottom>
          Space role
        </Typography>
        {detail.spaceRole ? (
          <Chip label={SPACE_ROLE_LABELS[detail.spaceRole] ?? detail.spaceRole} color="primary" size="small" />
        ) : (
          <Chip label="No role — no grant matched" size="small" />
        )}
      </Paper>

      <Stack direction="row" spacing={2}>
        <PermissionOutcome label="View" allowed={detail.canView} denialReason={detail.viewDenialReason} />
        <PermissionOutcome label="Edit" allowed={detail.canEdit} denialReason={detail.editDenialReason} />
      </Stack>

      <RestrictionList title="View restrictions" restrictions={detail.viewRestrictions} />
      <RestrictionList title="Edit restrictions" restrictions={detail.editRestrictions} />
    </Stack>
  )
}

function PermissionOutcome({
  label,
  allowed,
  denialReason,
}: {
  label: string
  allowed: boolean
  denialReason: string | null
}) {
  return (
    <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: allowed ? 0 : 1 }}>
        {allowed ? (
          <CheckCircleOutlineIcon color="success" fontSize="small" />
        ) : (
          <CancelOutlinedIcon color="error" fontSize="small" />
        )}
        <Typography variant="subtitle2">
          {label}: {allowed ? 'allowed' : 'denied'}
        </Typography>
      </Stack>
      {!allowed && (
        <Typography variant="body2" color="text.secondary">
          {describeDenialReason(denialReason)}
        </Typography>
      )}
    </Paper>
  )
}

function RestrictionList({ title, restrictions }: { title: string; restrictions: RestrictionCheck[] }) {
  if (restrictions.length === 0) {
    return (
      <Box>
        <Typography variant="subtitle2" color="text.secondary">
          {title}
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>
          None on this page or its ancestors.
        </Typography>
      </Box>
    )
  }

  return (
    <Box>
      <Typography variant="subtitle2" color="text.secondary" gutterBottom>
        {title}
      </Typography>
      <Stack spacing={1}>
        {restrictions.map((r) => (
          <Paper key={r.ruleId} variant="outlined" sx={{ p: 1.5 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
              {r.passed ? (
                <CheckCircleOutlineIcon color="success" fontSize="small" />
              ) : (
                <CancelOutlinedIcon color="error" fontSize="small" />
              )}
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {r.pageTitle}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                ({r.passed ? 'passed' : 'failed'})
              </Typography>
            </Stack>
            <RuleExpressionOrUnreadable node={r.expression} />
          </Paper>
        ))}
      </Stack>
    </Box>
  )
}
