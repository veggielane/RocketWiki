import { useRef, useState } from 'react'
import { Link as RouterLink, useSearchParams } from 'react-router-dom'
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  CircularProgress,
  Link,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import SendIcon from '@mui/icons-material/Send'
import { visuallyHidden } from '@mui/utils'
import { useClient } from 'urql'
import {
  AskWikiDocument,
  type AskWikiQuery,
  type AskWikiQueryVariables,
  type AskWikiUnavailableReason,
} from '../graphql/generated/graphql'
import { citationHref, type AskCitation } from '../ask/answerSegments'
import { AnswerBody } from '../ask/AnswerBody'
import { isAskWikiMarkedNotConfigured, markAskWikiNotConfigured, useAskWikiPossiblyAvailable } from '../ask/askAvailability'
import { describeAskUnavailable } from '../feedback/unavailableCopy'

/**
 * "Ask the wiki" (design.md §9.5) — single-question RAG over pages the
 * asker can view. v1 is stateless and non-streaming: each ask is one
 * `askWiki` query, multi-second, no conversation memory server-side. The
 * transcript below is therefore pure component state — navigate away and
 * it's gone, which the hint under the heading says out loud.
 *
 * Every designed failure mode arrives as a payload fact (`unavailable`),
 * never a GraphQL error, and renders as designed UX: NOT_CONFIGURED flips
 * the session-level availability store (the attempt is the probe — see
 * askAvailability.ts) and this page shows the honest feature-absent copy;
 * UNREACHABLE gets a retry; NO_RESULTS gets honest "nothing you can view
 * answers this" copy plus a keyword-search escape hatch.
 */

type EntryResult =
  | { state: 'pending' }
  | { state: 'answered'; answer: string; citations: AskCitation[] }
  | { state: 'unavailable'; reason: AskWikiUnavailableReason }
  /** Transport-level failure reaching our own API — not a designed payload, but still retryable UX, not a raw toast. */
  | { state: 'failed' }

interface TranscriptEntry {
  id: number
  question: string
  result: EntryResult
}

function toResult(payload: AskWikiQuery['askWiki'] | undefined): EntryResult {
  if (payload?.unavailable != null) return { state: 'unavailable', reason: payload.unavailable }
  if (payload?.answer != null) return { state: 'answered', answer: payload.answer, citations: payload.citations }
  return { state: 'failed' }
}

export function AskWikiPage() {
  const client = useClient()
  const [params] = useSearchParams()
  // Prefill from the search page's "Can't find it?" nudge — prefill only,
  // never auto-submit (an ask is a multi-second model call).
  const [draft, setDraft] = useState(() => params.get('q') ?? '')
  const [entries, setEntries] = useState<TranscriptEntry[]>([])
  const nextId = useRef(1)
  const possiblyAvailable = useAskWikiPossiblyAvailable()

  const busy = entries.some((e) => e.result.state === 'pending')
  // What the persistent live region below announces. The failure states
  // need no entry here: they render as role="alert" Alerts, which announce
  // themselves.
  const lastEntry = entries[entries.length - 1]
  const liveMessage = busy ? 'Looking for an answer…' : lastEntry?.result.state === 'answered' ? 'Answer ready.' : ''

  const runAsk = async (id: number, question: string) => {
    // Imperative query with the *generated* document + types: the generated
    // `useAskWikiQuery` hook models one live query, while this page keeps N
    // completed asks on screen — so each ask executes once, network-only
    // (re-asking the same question must not answer from cache), and lands
    // in the transcript entry it belongs to.
    const result = await client
      .query<AskWikiQuery, AskWikiQueryVariables>(AskWikiDocument, { question }, { requestPolicy: 'network-only' })
      .toPromise()
    const payload = result.data?.askWiki
    setEntries((prev) => prev.map((e) => (e.id === id ? { ...e, result: toResult(payload) } : e)))
    if (payload?.unavailable === 'NOT_CONFIGURED') markAskWikiNotConfigured()
  }

  const ask = () => {
    const question = draft.trim()
    if (question.length === 0 || busy || isAskWikiMarkedNotConfigured()) return
    const id = nextId.current++
    setEntries((prev) => [...prev, { id, question, result: { state: 'pending' } }])
    setDraft('')
    void runAsk(id, question)
  }

  const retry = (entry: TranscriptEntry) => {
    setEntries((prev) => prev.map((e) => (e.id === entry.id ? { ...e, result: { state: 'pending' } } : e)))
    void runAsk(entry.id, entry.question)
  }

  return (
    <Stack spacing={3}>
      <Box>
        <Typography variant="h4" component="h1">
          Ask the wiki
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          Answers draw only on wiki pages you can view. Nothing here is saved — the conversation disappears when you
          leave this page.
        </Typography>
      </Box>

      {/* Persistent polite live region for the multi-second wait: it must
          exist BEFORE the wait begins — a live region inserted together
          with its content is routinely not announced (announcement is
          about mutations inside an existing region). Visually hidden; the
          pending row in the transcript carries the visible spinner+copy. */}
      <Box role="status" sx={visuallyHidden}>
        {liveMessage}
      </Box>

      {entries.length > 0 && (
        <Stack component="ol" spacing={3} aria-label="Questions and answers" sx={{ listStyle: 'none', m: 0, p: 0 }}>
          {entries.map((entry) => (
            <Stack component="li" key={entry.id} spacing={1.5}>
              <Paper variant="outlined" sx={{ p: 1.5, bgcolor: 'action.hover' }}>
                <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontWeight: 600 }}>
                  {entry.question}
                </Typography>
              </Paper>
              <TranscriptResult entry={entry} onRetry={() => retry(entry)} />
            </Stack>
          ))}
        </Stack>
      )}

      {possiblyAvailable ? (
        <Paper
          component="form"
          variant="outlined"
          sx={{ p: 2 }}
          onSubmit={(e) => {
            e.preventDefault()
            ask()
          }}
        >
          <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
            <TextField
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                // Enter submits; Shift+Enter falls through to the textarea's
                // default and inserts a newline. No length cap — the server
                // budgets model context, not us.
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  ask()
                }
              }}
              label="Ask a question"
              multiline
              minRows={1}
              maxRows={8}
              fullWidth
              autoFocus
              helperText="Enter to ask · Shift+Enter for a new line"
            />
            <Button
              type="submit"
              variant="contained"
              endIcon={<SendIcon />}
              disabled={busy || draft.trim().length === 0}
              sx={{ mt: 1 }}
            >
              Ask
            </Button>
          </Stack>
        </Paper>
      ) : (
        <Alert severity="info">
          <AlertTitle>The wiki assistant isn't available on this instance</AlertTitle>
          Ask the wiki needs an instance-level chat model endpoint, and none is configured here. Keyword search still
          covers everything you can view —{' '}
          <Link component={RouterLink} to="/search">
            go to search
          </Link>
          .
        </Alert>
      )}
    </Stack>
  )
}

function TranscriptResult({ entry, onRetry }: { entry: TranscriptEntry; onRetry: () => void }) {
  const { result, question } = entry
  switch (result.state) {
    case 'pending':
      // No role="status" here: this row mounts WITH its content, which live
      // regions don't reliably announce — the page-level persistent region
      // above owns the announcement; this row is the visible counterpart.
      return (
        <Stack direction="row" spacing={1.5} sx={{ pl: 0.5, alignItems: 'center' }}>
          <CircularProgress size={18} aria-hidden />
          <Typography variant="body2" color="text.secondary">
            Searching the wiki and composing an answer — this can take several seconds.
          </Typography>
        </Stack>
      )
    case 'answered':
      return (
        <Stack spacing={1}>
          <AnswerBody answer={result.answer} citations={result.citations} />
          {result.citations.length > 0 && (
            <Box>
              <Typography variant="overline" component="h2" color="text.secondary">
                Sources
              </Typography>
              <List dense disablePadding>
                {result.citations.map((citation, i) => (
                  // ListItem (an <li>) wraps the link — a bare <a> as a direct
                  // <ul> child is invalid list markup (WCAG 1.3.1 / axe "list").
                  <ListItem key={i} disablePadding>
                    <ListItemButton
                      component={RouterLink}
                      to={citationHref(citation)}
                      sx={{ borderRadius: 1, alignItems: 'baseline', gap: 1 }}
                    >
                      <Typography variant="caption" color="primary" sx={{ fontWeight: 700, flexShrink: 0 }}>
                        S{i + 1}
                      </Typography>
                      <ListItemText
                        primary={citation.title}
                        secondary={citation.headingPath.length > 0 ? citation.headingPath.join(' › ') : undefined}
                        sx={{ my: 0 }}
                      />
                    </ListItemButton>
                  </ListItem>
                ))}
              </List>
            </Box>
          )}
        </Stack>
      )
    case 'unavailable':
      // Summaries come from the shared degradation vocabulary
      // (feedback/unavailableCopy.ts); this switch only owns the per-reason
      // affordances — the search escape hatch, the retry button.
      switch (result.reason) {
        case 'NO_RESULTS':
          return (
            <Stack spacing={0.5}>
              <Typography>{describeAskUnavailable('NO_RESULTS').summary}</Typography>
              <Typography variant="body2" color="text.secondary">
                It may not be written down — or it's on pages you don't have access to. Try a{' '}
                <Link component={RouterLink} to={`/search?q=${encodeURIComponent(question)}`}>
                  keyword search
                </Link>{' '}
                instead.
              </Typography>
            </Stack>
          )
        case 'UNREACHABLE':
          return (
            <Alert
              severity="warning"
              action={
                <Button color="inherit" size="small" onClick={onRetry}>
                  Retry
                </Button>
              }
            >
              {describeAskUnavailable('UNREACHABLE').summary}
            </Alert>
          )
        case 'NOT_CONFIGURED':
          // The page-level alert (rendered where the composer was) carries
          // the full explanation; this keeps the transcript honest.
          return <Typography color="text.secondary">{describeAskUnavailable('NOT_CONFIGURED').summary}</Typography>
      }
      break
    case 'failed':
      return (
        <Alert
          severity="warning"
          action={
            <Button color="inherit" size="small" onClick={onRetry}>
              Retry
            </Button>
          }
        >
          {describeAskUnavailable('REQUEST_FAILED').summary}
        </Alert>
      )
  }
}
