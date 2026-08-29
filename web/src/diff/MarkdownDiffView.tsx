import { useMemo } from 'react'
import { Box, Typography } from '@mui/material'
import { alpha, type Theme } from '@mui/material/styles'
import { computeStaleDiff, isUnchanged, type DiffLine } from './staleDiff'

export interface MarkdownDiffViewProps {
  /**
   * The BEFORE side — raw Markdown. Named for the comparison rather than for one
   * of its callers: this started life showing "your draft" against "their save"
   * during an edit conflict, and the history screen compares two saved revisions
   * where neither is anyone's draft. A prop called `yourDraft` would have made
   * the second caller read as though it were the first.
   */
  before: string
  /** The AFTER side — raw Markdown. */
  after: string
  /** Accessible name for the scrollable diff region. */
  label: string
}

const lineSx = {
  unchanged: {},
  added: { bgcolor: (theme: Theme) => alpha(theme.palette.success.main, 0.12) },
  removed: { bgcolor: (theme: Theme) => alpha(theme.palette.error.main, 0.12) },
} as const

const segmentSx = {
  added: { bgcolor: (theme: Theme) => alpha(theme.palette.success.main, 0.3) },
  removed: { bgcolor: (theme: Theme) => alpha(theme.palette.error.main, 0.3) },
} as const

/** '+' their line, '−' your line — literal text, so it survives copy-paste and screen readers alike. */
const marker = { unchanged: ' ', added: '+', removed: '−' } as const

function DiffLineRow({ line }: { line: DiffLine }) {
  const changedSx = line.kind === 'unchanged' ? null : segmentSx[line.kind]
  return (
    <Box component="div" sx={{ display: 'flex', px: 1, ...lineSx[line.kind] }}>
      <Box component="span" sx={{ flexShrink: 0, width: '1.25em', userSelect: 'none', opacity: 0.7 }}>
        {marker[line.kind]}
      </Box>
      <Box component="span" sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', minHeight: '1.4em' }}>
        {line.segments && changedSx
          ? line.segments.map((segment, i) =>
              segment.changed ? (
                <Box key={i} component="mark" sx={{ color: 'inherit', ...changedSx }}>
                  {segment.text}
                </Box>
              ) : (
                <span key={i}>{segment.text}</span>
              ),
            )
          : line.text}
      </Box>
    </Box>
  )
}

/**
 * Read-only comparison of two pieces of Markdown, line-based with word-level
 * marks inside changed lines (see staleDiff.ts). Two callers: the stale-revision
 * merge flow, which sets a draft against the save that overtook it, and the
 * history screen, which sets two saved revisions against each other.
 *
 * Both inputs are page content — inside the page-content trust boundary —
 * so every character is rendered as React text nodes; this component must
 * never interpret them as HTML or Markdown.
 */
export function MarkdownDiffView({ before, after, label }: MarkdownDiffViewProps) {
  const lines = useMemo(() => computeStaleDiff(before, after), [before, after])

  if (isUnchanged(lines)) {
    return (
      <Typography variant="body2" color="text.secondary">
        The two versions are identical.
      </Typography>
    )
  }

  return (
    <Box
      role="region"
      aria-label={label}
      tabIndex={0}
      sx={{
        maxHeight: '45vh',
        overflow: 'auto',
        border: 1,
        borderColor: 'divider',
        borderRadius: 1,
        py: 0.5,
        fontFamily: 'monospace',
        fontSize: '0.8125rem',
        lineHeight: 1.5,
      }}
    >
      {lines.map((line, i) => (
        <DiffLineRow key={i} line={line} />
      ))}
    </Box>
  )
}
