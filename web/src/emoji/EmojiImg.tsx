import { useEffect, useState } from 'react'
import { Box } from '@mui/material'
import { getEmojiUrl, peekEmojiUrl } from './emojiBlobCache'

export interface EmojiImgProps {
  name: string
  etag: string
  /** Rendered height in px (width follows the aspect ratio; server guarantees ≤2:1). */
  size?: number
}

/**
 * React-side emoji image for chrome surfaces (toolbar picker, suggestion
 * popup, admin list) — the in-content renderer is the ProseMirror
 * decoration (editor/emoji/EmojiDecorations.ts), which builds its DOM
 * directly. Both share the same (name, etag) blob cache. While the blob
 * loads (or if it can't), the literal `:name:` text renders instead — the
 * same degrade-to-text rule as content.
 *
 * MUI Box, not a raw styled <img>: this is app chrome, so it follows the
 * MUI-first rule (the plain-CSS boundary is editor internals only).
 */
export function EmojiImg({ name, etag, size = 20 }: EmojiImgProps) {
  const peek = () => peekEmojiUrl(name, etag) ?? null
  const [url, setUrl] = useState<string | null>(peek)
  // Render-time reset when the identity props change (React's documented
  // "adjusting state when a prop changes" pattern — same as UserAvatar and
  // useAttachmentBlobUrl). Without it, a reused mount showed the PREVIOUS
  // emoji's image for a frame after `name` changed, until the effect below
  // caught up.
  const [tracked, setTracked] = useState({ name, etag })
  if (tracked.name !== name || tracked.etag !== etag) {
    setTracked({ name, etag })
    setUrl(peek())
  }

  useEffect(() => {
    let cancelled = false
    void getEmojiUrl(name, etag).then((resolved) => {
      if (!cancelled) setUrl(resolved)
    })
    return () => {
      cancelled = true
    }
  }, [name, etag])

  if (!url) {
    return <span>{`:${name}:`}</span>
  }
  return (
    <Box
      component="img"
      src={url}
      alt={`:${name}:`}
      title={`:${name}:`}
      sx={{ height: size, verticalAlign: 'middle' }}
    />
  )
}
