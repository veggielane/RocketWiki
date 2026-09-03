import { useId, useState } from 'react'
import { Box, Collapse, IconButton, List, ListItem, ListItemText, Typography } from '@mui/material'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import type { ClassificationLevel } from '../../graphql/generated/graphql'
import { MarkingLabelChip } from '../../markings/MarkingLabelChip'
import { accessGateTitle, describeAccessGate, type GateCheck } from './describeAccessGate'
import { NO_SPACE_ACCESS, WHY_PROTECTED_BUTTON } from './protectedCopy'

/** The subset of `AccessDenial` a leaf renders. Structural, so both tree documents' generated shapes satisfy it. */
export interface ProtectedLeafDenial {
  placeholderTitle: string
  noSpaceAccess: boolean
  marking: { label: string; level: ClassificationLevel } | null
  reasons: readonly GateCheck[]
}

export interface ProtectedTreeLeafProps {
  denial: ProtectedLeafDenial
  /** Left padding of the row, in theme spacing units — the caller's own depth arithmetic, so a leaf lines up with its readable siblings. */
  indent: number
  /** The rail keeps a 24px column for its disclosure chevrons; a leaf in it keeps the column too, so its text lines up. */
  leadingSpacer?: boolean
}

/**
 * A page this caller may not read, at its place in a tree (design.md §6.7 /
 * §21.8). It says three things and no more: that a page is here (the
 * server's placeholder title, verbatim), how it is marked (the label, when
 * the caller holds access to the space), and why they cannot read it (every
 * failing gate, behind one disclosure).
 *
 * Not a link, no menu, and not otherwise focusable — there is nothing to
 * open. The ONE control is the disclosure button, and it is there because a
 * tooltip on a non-focusable row reaches no keyboard user and a wholly inert
 * row hides the reasons from them. `aria-expanded` carries its state, so
 * which glyph is drawn is never the only signal (WCAG 1.4.1).
 *
 * With no access to the space at all the marking is withheld and the reasons
 * collapse to one sentence; the leaf then shows the title and that sentence
 * and nothing to disclose. (The server answers an empty tree in that state
 * anyway — the space-wide note above the tree is the ordinary rendering —
 * but a leaf that arrives that way must still render honestly.)
 */
export function ProtectedTreeLeaf({ denial, indent, leadingSpacer = false }: ProtectedTreeLeafProps) {
  const [open, setOpen] = useState(false)
  const reasonsId = useId()

  return (
    <li>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, pl: indent, py: 0.25, minWidth: 0 }}>
        {leadingSpacer && <Box sx={{ width: 24, flexShrink: 0 }} />}
        {/* Decorative: the title beside it already says what this row is, and
            the disclosure button's own name says why. */}
        <LockOutlinedIcon aria-hidden sx={{ fontSize: 16, flexShrink: 0, color: 'text.secondary' }} />
        <Typography
          component="span"
          variant="body2"
          color="text.secondary"
          noWrap
          sx={{ fontStyle: 'italic', minWidth: 0 }}
        >
          {denial.placeholderTitle}
        </Typography>
        {denial.marking && <MarkingLabelChip label={denial.marking.label} level={denial.marking.level} />}
        {!denial.noSpaceAccess && (
          <IconButton
            size="small"
            aria-label={WHY_PROTECTED_BUTTON}
            aria-expanded={open}
            aria-controls={reasonsId}
            onClick={() => setOpen((previous) => !previous)}
            // WCAG 2.5.8's floor, same as the rail's chevrons: `size="small"`
            // around a 16px glyph lands at 20px on its own.
            sx={{ p: 0, width: 24, height: 24, flexShrink: 0 }}
          >
            <InfoOutlinedIcon sx={{ fontSize: 16 }} />
          </IconButton>
        )}
      </Box>
      {denial.noSpaceAccess ? (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', pl: indent + (leadingSpacer ? 3 : 0) + 3 }}>
          {NO_SPACE_ACCESS}
        </Typography>
      ) : (
        <Collapse in={open}>
          <List dense disablePadding id={reasonsId} sx={{ pl: indent + (leadingSpacer ? 3 : 0) + 3 }}>
            {denial.reasons.map((reason, index) => (
              <ListItem key={`${reason.gate}-${index}`} disablePadding sx={{ py: 0.25 }}>
                <ListItemText
                  primary={`${accessGateTitle(reason.gate)}: ${describeAccessGate(reason)}`}
                  slotProps={{ primary: { variant: 'caption', color: 'text.secondary' } }}
                />
              </ListItem>
            ))}
          </List>
        </Collapse>
      )}
    </li>
  )
}
