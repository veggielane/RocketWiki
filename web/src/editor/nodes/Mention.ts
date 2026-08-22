import { Node, mergeAttributes } from '@tiptap/core'

/**
 * `@[display](user://{id})` — an atomic, non-editable mention chip (design.md
 * §4 and §8's "mentions parsed from `user://{id}` links on save"). Modeled
 * as an atomic inline node rather than a mark over editable text: the
 * display name is a snapshot, not something a user should be able to edit
 * character-by-character and drift out of sync with the referenced user.
 */
export interface MentionOptions {
  HTMLAttributes: Record<string, unknown>
}

export interface MentionAttributes {
  userId: string
  display: string
}

export const Mention = Node.create<MentionOptions>({
  name: 'mention',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,

  addOptions() {
    return { HTMLAttributes: {} }
  },

  addAttributes() {
    return {
      userId: { default: null },
      display: { default: null },
    }
  },

  parseHTML() {
    return [
      {
        tag: 'span[data-mention-user-id]',
        getAttrs: (element) => {
          if (!(element instanceof HTMLElement)) return false
          return {
            userId: element.getAttribute('data-mention-user-id'),
            display: element.textContent?.replace(/^@/, '') ?? '',
          }
        },
      },
    ]
  },

  renderHTML({ node, HTMLAttributes }) {
    return [
      'span',
      mergeAttributes(this.options.HTMLAttributes, HTMLAttributes, {
        'data-mention-user-id': node.attrs.userId as string,
        class: 'rw-mention',
      }),
      `@${node.attrs.display as string}`,
    ]
  },

  renderText({ node }) {
    return `@${node.attrs.display as string}`
  },
})
