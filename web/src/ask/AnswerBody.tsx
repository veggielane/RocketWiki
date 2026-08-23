import { Fragment } from 'react'
import { Link, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { citationHref, segmentAnswer, type AskCitation } from './answerSegments'

/**
 * The answer text with `[Sn]` markers converted to superscript citation
 * links. The answer is untrusted model output rendered strictly as React
 * TEXT nodes — escaped by construction, no dangerouslySetInnerHTML, and
 * (v1 tradeoff, stated) no Markdown rendering: a model that emits
 * `**bold**` or `<script>` shows it literally. Routing model output
 * through the real Markdown pipeline would also blur the one-renderer
 * rule's trust boundary (page Markdown is author content; this is not).
 * `white-space: pre-wrap` keeps the model's own paragraph breaks legible
 * without interpreting anything.
 */
export function AnswerBody({ answer, citations }: { answer: string; citations: readonly AskCitation[] }) {
  return (
    <Typography component="div" sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
      {segmentAnswer(answer, citations).map((segment, i) =>
        segment.kind === 'text' ? (
          <Fragment key={i}>{segment.text}</Fragment>
        ) : (
          <Link
            key={i}
            component={RouterLink}
            to={citationHref(segment.citation)}
            aria-label={`Source ${segment.n}: ${segment.citation.title}`}
            sx={{
              verticalAlign: 'super',
              fontSize: '0.72em',
              lineHeight: 1,
              mx: '1px',
              fontWeight: 600,
              textDecoration: 'none',
            }}
          >
            [S{segment.n}]
          </Link>
        ),
      )}
    </Typography>
  )
}
