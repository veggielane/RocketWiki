import { NodeViewWrapper } from '@tiptap/react'
import type { NodeViewProps } from '@tiptap/core'
import { useAttachmentBlobUrl } from '../../attachments/useAttachmentBlobUrl'

function extractAttachmentId(src: string): string | null {
  return src.startsWith('attachment://') ? src.slice('attachment://'.length) : null
}

export function AttachmentImageView({ node }: NodeViewProps) {
  const attachmentId = extractAttachmentId(node.attrs.src as string)
  const state = useAttachmentBlobUrl(attachmentId)

  return (
    <NodeViewWrapper as="span" className="rw-attachment-image">
      {state.status === 'loading' && <span className="rw-attachment-image-placeholder">Loading image…</span>}
      {/* Same placeholder whether the attachment doesn't exist or the
          viewer just can't see it — design.md §6.7 applies to attachments
          exactly like pages (see attachmentApi.ts). */}
      {state.status === 'error' && <span className="rw-attachment-image-placeholder">Image unavailable</span>}
      {state.status === 'ready' && (
        <img
          src={state.url}
          alt={(node.attrs.alt as string | null) ?? ''}
          title={(node.attrs.title as string | null) ?? undefined}
        />
      )}
    </NodeViewWrapper>
  )
}
