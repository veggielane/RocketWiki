import { Node, mergeAttributes } from '@tiptap/core'

/** `:::info … :::` directive blocks (design.md §4). */
export const CALLOUT_TYPES = ['info', 'warning', 'note'] as const
export type CalloutType = (typeof CALLOUT_TYPES)[number]

export interface CalloutOptions {
  HTMLAttributes: Record<string, unknown>
}

export interface CalloutAttributes {
  calloutType: CalloutType
}

export const Callout = Node.create<CalloutOptions>({
  name: 'callout',
  group: 'block',
  content: 'block+',
  defining: true,

  addOptions() {
    return { HTMLAttributes: {} }
  },

  addAttributes() {
    return {
      calloutType: {
        default: 'info',
        parseHTML: (element) => element.getAttribute('data-callout-type') ?? 'info',
        renderHTML: (attributes: Record<string, unknown>) => ({
          'data-callout-type': attributes.calloutType,
        }),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'div[data-callout-type]' }]
  },

  renderHTML({ HTMLAttributes }) {
    return [
      'div',
      mergeAttributes(this.options.HTMLAttributes, HTMLAttributes, { class: 'rw-callout' }),
      0,
    ]
  },
})
