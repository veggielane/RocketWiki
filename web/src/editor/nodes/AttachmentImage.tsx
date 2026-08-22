import Image from '@tiptap/extension-image'
import { ReactNodeViewRenderer } from '@tiptap/react'
import { AttachmentImageView } from './AttachmentImageView'

/**
 * Extends the stock Image node with a NodeView that resolves
 * `attachment://{id}` to a displayable image (attachmentApi.ts /
 * useAttachmentBlobUrl.ts) instead of rendering the URI scheme verbatim as
 * a broken `<img src>` — the browser has no idea what `attachment://`
 * means, only our own fetch-with-auth layer does. Markdown parse/serialize
 * is untouched: this only changes how the node *renders* in the editor.
 */
export const AttachmentImage = Image.extend({
  addNodeView() {
    return ReactNodeViewRenderer(AttachmentImageView)
  },
})
