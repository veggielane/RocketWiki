import { useEffect, useState } from 'react'
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
 */
export function EmojiImg({ name, etag, size = 20 }: EmojiImgProps) {
  const [url, setUrl] = useState<string | null>(() => peekEmojiUrl(name, etag) ?? null)

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
  return <img src={url} alt={`:${name}:`} title={`:${name}:`} style={{ height: size, verticalAlign: 'middle' }} />
}
