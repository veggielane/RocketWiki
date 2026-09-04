import { Alert, Box, Chip, List, ListItem, ListItemIcon, ListItemText, Paper, Stack, Typography } from '@mui/material'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutlined'
import CancelOutlinedIcon from '@mui/icons-material/CancelOutlined'
import { RuleExpressionOrUnreadable } from '../RuleExpressionSummary'
import { accessGateTitle, describeAccessGate, type GateCheck } from '../denial/describeAccessGate'
import { describeDenialReason } from './describeDenialReason'
import type { EffectivePermissionDetail, RestrictionCheck, SpaceRole } from './effectivePermissionTypes'

const SPACE_ROLE_LABELS: Record<SpaceRole, string> = {
  editor: 'Editor',
  spaceAdmin: 'Space admin',
}

/**
 * design.md §6.6's permission inspector: "why can/can't user X see this
 * page" — the space-access and space-role computations (§6.4's two halves),
 * every gate on the view and edit ladders with pass/fail, and every
 * restriction with pass/fail per condition. Read-only; there is nothing here
 * to edit (rule editing is `RuleBuilder`'s job).
 */
export function PermissionInspector({ detail }: { detail: EffectivePermissionDetail }) {
  return (
    <Stack spacing={2}>
      <Typography variant="h6" component="h2">
        Why can {detail.userDisplayName} {detail.canView ? '' : "n't"} see this page?
      </Typography>

      {detail.isReplicaSpace && (
        <Alert severity="info">
          This space is a replica of another instance — it's read-only regardless of any grant.
        </Alert>
      )}

      <Stack direction="row" spacing={2}>
        <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
          <Typography variant="subtitle2" color="text.secondary" gutterBottom>
            Space access
          </Typography>
          {/* Whether an ACCESS grant matched — the gate that lets this user see
              anything here at all. Separate from the role on purpose: a role
              says nothing about visibility (§6.4). */}
          {detail.hasSpaceAccess ? (
            <Chip label="Granted" color="primary" size="small" />
          ) : (
            <Chip label="No access grant matched" size="small" />
          )}
        </Paper>
        <Paper variant="outlined" sx={{ p: 2, flex: 1 }}>
          <Typography variant="subtitle2" color="text.secondary" gutterBottom>
            Space role
          </Typography>
          {detail.spaceRole ? (
            <Chip label={SPACE_ROLE_LABELS[detail.spaceRole]} color="primary" size="small" />
          ) : (
            // No role is the ordinary state for a reader, so it is not an
            // absence to explain — just "None".
            <Chip label="None" size="small" />
          )}
        </Paper>
      </Stack>

      <Stack direction="row" spacing={2}>
        <PermissionOutcome label="View" allowed={detail.canView} denialReason={detail.viewDenialReason} />
        <PermissionOutcome label="Edit" allowed={detail.canEdit} denialReason={detail.editDenialReason} />
      </Stack>

      <GateList title="View gates" gates={detail.viewGates} />
      <GateList title="Edit gates" gates={detail.editGates} />

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

/**
 * The whole ladder, every gate evaluated. The server's explain path does not
 * short-circuit, so a user who fails the selector gate still sees whether
 * the caveat would have admitted them — which is the difference between
 * "one thing to fix" and "three".
 */
function GateList({ title, gates }: { title: string; gates: GateCheck[] }) {
  if (gates.length === 0) return null
  return (
    <Box>
      <Typography variant="subtitle2" color="text.secondary" gutterBottom>
        {title}
      </Typography>
      <List dense disablePadding aria-label={title}>
        {gates.map((gate, index) => (
          <ListItem key={`${gate.gate}-${index}`} disableGutters sx={{ alignItems: 'flex-start' }}>
            <ListItemIcon sx={{ minWidth: 32, mt: 0.5 }}>
              {gate.passed ? (
                <CheckCircleOutlineIcon color="success" fontSize="small" titleAccess="Passed" />
              ) : (
                <CancelOutlinedIcon color="error" fontSize="small" titleAccess="Failed" />
              )}
            </ListItemIcon>
            <ListItemText primary={accessGateTitle(gate.gate)} secondary={describeAccessGate(gate)} />
          </ListItem>
        ))}
      </List>
    </Box>
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
