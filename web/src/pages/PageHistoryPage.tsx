import { Fragment, useState } from 'react'
import { useParams } from 'react-router-dom'
import {
  Alert,
  Chip,
  Divider,
  Paper,
  Radio,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import { usePageHistoryQuery } from '../graphql/generated/graphql'
import { describeLoadFailure } from '../feedback/unavailableCopy'
import { PageHeader } from '../app/PageHeader'
import { useDocumentTitle } from '../app/documentTitle'
import { MarkdownDiffView } from '../diff/MarkdownDiffView'
import { UserLink } from '../users/UserLink'

interface Revision {
  id: string
  revisionNumber: number
  title: string
  content: string
  editSummary?: string | null
  createdAtUtc: string
  author: { id: string; displayName: string; hasAvatar: boolean }
  contributors: { id: string; displayName: string; hasAvatar: boolean }[]
}

/**
 * A page's revision history: who saved what, when, and what changed between any
 * two of them.
 *
 * **Not gated on `canEdit`, unlike the details screen.** History is a read of
 * content the caller can already see — every revision's text is a past state of
 * the page in front of them — so gating it would withhold the provenance of
 * something they are looking at. The gate that matters already happened: the
 * page read itself, which returns null for a page this caller may not view, and
 * takes its revisions with it.
 *
 * The diff is computed in the browser from two revisions already fetched. That
 * is not a shortcut around the server — both texts came through the same
 * permission-checked page read, so comparing them locally discloses nothing the
 * caller was not already handed.
 */
export function PageHistoryPage() {
  const { pageId } = useParams<{ pageId: string }>()
  const [{ data, fetching, error }] = usePageHistoryQuery({
    variables: { id: pageId ?? '' },
    pause: !pageId,
  })
  const [fromId, setFromId] = useState<string | null>(null)
  const [toId, setToId] = useState<string | null>(null)

  useDocumentTitle(data?.page ? `History — ${data.page.title}` : 'History')

    // FIRST LOAD ONLY. urql retains `data` across a refetch and flips `fetching`
  // true (urql.js computeNextState), so a bare `if (fetching)` threw the screen
  // away on every post-write refetch: content, scroll position and keyboard
  // focus all went with it. `&& !data` keeps the rendered screen up while the
  // re-read happens underneath it.
  if (fetching && !data) {
    return (
      <Stack spacing={1}>
        <Skeleton variant="text" width="40%" height={48} />
        <Skeleton variant="rectangular" height={320} />
      </Stack>
    )
  }
  if (error || !data?.page) {
    // Absent and not-viewable stay indistinguishable (design.md §6.7).
    return <Alert severity="info">{describeLoadFailure('PAGE').summary}</Alert>
  }

  const page = data.page
  // Newest first: the question people arrive with is "what changed recently",
  // not "how did this begin".
  const revisions = [...(page.revisions as Revision[])].sort((a, b) => b.revisionNumber - a.revisionNumber)

  // Default to the two most recent, which is the comparison almost everyone
  // wants and saves two clicks to reach it.
  const to = revisions.find((r) => r.id === toId) ?? revisions[0]
  const from = revisions.find((r) => r.id === fromId) ?? revisions[1]

  return (
    <Stack spacing={3}>
      <PageHeader title="History" subject={{ label: page.title, to: `/pages/${page.id}` }} />

      <Paper variant="outlined">
        <Table size="small" aria-label="Revisions">
          <TableHead>
            <TableRow>
              {/* Two columns of radios rather than checkboxes: this is one
                  comparison with two ends, not a multi-select. The headers name
                  which end each column picks, since a bare radio in a table says
                  nothing about what choosing it does. */}
              <TableCell>From</TableCell>
              <TableCell>To</TableCell>
              <TableCell>Revision</TableCell>
              <TableCell>Saved by</TableCell>
              <TableCell>When</TableCell>
              <TableCell>Summary</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {revisions.map((revision) => (
              <TableRow key={revision.id}>
                <TableCell padding="checkbox">
                  <Radio
                    size="small"
                    checked={from?.id === revision.id}
                    onChange={() => setFromId(revision.id)}
                    // Named per row: several radios in one column would otherwise
                    // share an accessible name and be indistinguishable by voice.
                    // `slotProps.input`, not the older `inputProps` — that one still
                    // typechecks in MUI 9 but never reaches the DOM, so the radios
                    // ship nameless and only the axe test notices.
                    slotProps={{ input: { 'aria-label': `Compare from revision ${revision.revisionNumber}` } }}
                  />
                </TableCell>
                <TableCell padding="checkbox">
                  <Radio
                    size="small"
                    checked={to?.id === revision.id}
                    onChange={() => setToId(revision.id)}
                    slotProps={{ input: { 'aria-label': `Compare to revision ${revision.revisionNumber}` } }}
                  />
                </TableCell>
                <TableCell>{revision.revisionNumber}</TableCell>
                <TableCell>
                  <Stack spacing={0.25} sx={{ alignItems: 'flex-start' }}>
                    <UserLink user={revision.author} avatarSize={24} variant="body2" />
                    {/* Who typed, when that differs from who saved — a co-edited
                        revision carries other people's live edits, and crediting
                        only the person who pressed save would be wrong about it.
                        Each name is a link like the author's: a contributor is a
                        person too, not a footnote. */}
                    {revision.contributors.filter((c) => c.id !== revision.author.id).length > 0 && (
                      <Typography variant="caption" color="text.secondary">
                        with{' '}
                        {revision.contributors
                          .filter((c) => c.id !== revision.author.id)
                          .map((c, index) => (
                            <Fragment key={c.id}>
                              {index > 0 && ', '}
                              <UserLink user={c} />
                            </Fragment>
                          ))}
                      </Typography>
                    )}
                  </Stack>
                </TableCell>
                <TableCell>
                  <Typography variant="body2" sx={{ whiteSpace: "nowrap" }}>
                    {new Date(revision.createdAtUtc).toLocaleString()}
                  </Typography>
                </TableCell>
                <TableCell>
                  {revision.editSummary ? (
                    <Typography variant="body2">{revision.editSummary}</Typography>
                  ) : (
                    <Typography variant="body2" color="text.secondary">
                      —
                    </Typography>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Paper>

      <Paper variant="outlined" sx={{ p: 2 }}>
        <Typography variant="h6" component="h2" gutterBottom>
          Changes
        </Typography>
        {from && to && from.id !== to.id ? (
          <>
            <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: "center", flexWrap: "wrap", mb: 1.5 }}>
              <Chip size="small" label={`Revision ${from.revisionNumber}`} />
              <Typography variant="body2" color="text.secondary">
                to
              </Typography>
              <Chip size="small" label={`Revision ${to.revisionNumber}`} />
              {from.title !== to.title && (
                <Typography variant="body2" color="text.secondary">
                  · title changed from "{from.title}" to "{to.title}"
                </Typography>
              )}
            </Stack>
            <Divider sx={{ mb: 1.5 }} />
            {/* Older is BEFORE whichever way round they were picked, so additions
                always read as "what arrived" rather than flipping meaning when
                someone selects bottom-up. */}
            <MarkdownDiffView
              before={(from.revisionNumber < to.revisionNumber ? from : to).content}
              after={(from.revisionNumber < to.revisionNumber ? to : from).content}
              label={`Changes between revision ${from.revisionNumber} and revision ${to.revisionNumber}`}
            />
          </>
        ) : (
          <Typography variant="body2" color="text.secondary">
            {revisions.length < 2
              ? 'This page has only one revision, so there is nothing to compare it with yet.'
              : 'Pick two different revisions to compare.'}
          </Typography>
        )}
      </Paper>
    </Stack>
  )
}
