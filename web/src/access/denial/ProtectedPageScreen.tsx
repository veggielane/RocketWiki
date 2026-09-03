import { Alert, List, ListItem, ListItemIcon, ListItemText, Paper, Stack, Typography } from '@mui/material'
import CancelOutlinedIcon from '@mui/icons-material/CancelOutlined'
import type { ClassificationLevel } from '../../graphql/generated/graphql'
import { PageHeader } from '../../app/PageHeader'
import { useDocumentTitle } from '../../app/documentTitle'
import { MarkingBanner } from '../../markings/MarkingBanner'
import { accessGateTitle, describeAccessGate, type GateCheck } from './describeAccessGate'
import { NO_SPACE_ACCESS, PROTECTED_PAGE_TITLE, WHY_PROTECTED_HEADING } from './protectedCopy'

/** The subset of `AccessDenial` the screen renders. Structural, so any query's generated shape satisfies it. */
export interface ProtectedPageDenial {
  noSpaceAccess: boolean
  marking: { label: string; level: ClassificationLevel } | null
  reasons: readonly GateCheck[]
}

export interface ProtectedPageScreenProps {
  denial: ProtectedPageDenial
  /**
   * The display spelling of the caller's own clearance, when the caller can
   * supply it (`me.clearance` looked up in `classificationScheme`). Lets the
   * clearance sentence say what they hold as well as what is needed. Null
   * or absent leaves the sentence without it — the SPA owns no spelling.
   */
  heldLevelName?: string | null
}

/**
 * The page-sized placeholder for a page this caller may not read (design.md
 * §6.7 / §21.8). Presentational: whoever ran the disclosing query hands the
 * denial in.
 *
 * The heading is "Protected page" and never the page's title — the caller
 * may not know it, and the denial does not carry it. That extends to the
 * browser tab: the document title is set here, to the same words, so a
 * bookmark or the history menu cannot name what the screen would not.
 *
 * Two shapes. With access to the space, the page's whole marking (the
 * server's label, verbatim, through the same banner a readable page uses)
 * and every failing gate as a sentence. Without it, one sentence and nothing
 * else — the marking is withheld and so is everything a marking would say.
 */
export function ProtectedPageScreen({ denial, heldLevelName = null }: ProtectedPageScreenProps) {
  useDocumentTitle(PROTECTED_PAGE_TITLE)

  return (
    <Stack spacing={2}>
      <PageHeader title={PROTECTED_PAGE_TITLE} />

      {denial.noSpaceAccess ? (
        <Alert severity="info">{NO_SPACE_ACCESS}</Alert>
      ) : (
        <>
          {denial.marking && <MarkingBanner label={denial.marking.label} level={denial.marking.level} placement="head" />}
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="h6" component="h2" gutterBottom>
              {WHY_PROTECTED_HEADING}
            </Typography>
            <List dense disablePadding>
              {denial.reasons.map((reason, index) => (
                <ListItem key={`${reason.gate}-${index}`} disableGutters sx={{ alignItems: 'flex-start' }}>
                  <ListItemIcon sx={{ minWidth: 32, mt: 0.5 }}>
                    {/* Decorative beside a sentence that already says "cannot";
                        a screen reader gets the title and the sentence. */}
                    <CancelOutlinedIcon color="error" fontSize="small" aria-hidden />
                  </ListItemIcon>
                  <ListItemText
                    primary={accessGateTitle(reason.gate)}
                    secondary={describeAccessGate(reason, { heldLevelName })}
                    slotProps={{ primary: { sx: { fontWeight: 600 } } }}
                  />
                </ListItem>
              ))}
            </List>
          </Paper>
        </>
      )}
    </Stack>
  )
}
