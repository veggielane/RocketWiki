import { Mark, mergeAttributes } from '@tiptap/core'

/**
 * `[title](page://{id})` — internal links to other wiki pages (design.md
 * §4). Kept as a distinct mark from the standard `link` (which StarterKit
 * registers for http/https only) so the editor can render page links with
 * their own affordance (icon, eventual title resolution/validity check)
 * without the standard Link extension's http(s)-only protocol allowlist
 * getting in the way.
 */
export interface PageLinkOptions {
  HTMLAttributes: Record<string, unknown>
}

export interface PageLinkAttributes {
  pageId: string
}

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    pageLink: {
      setPageLink: (attrs: PageLinkAttributes) => ReturnType
      unsetPageLink: () => ReturnType
    }
  }
}

export const PageLink = Mark.create<PageLinkOptions>({
  name: 'pageLink',

  addOptions() {
    return { HTMLAttributes: {} }
  },

  inclusive: false,

  addAttributes() {
    return {
      pageId: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-page-id'),
        renderHTML: (attributes: Record<string, unknown>) => ({
          'data-page-id': attributes.pageId,
        }),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'a[data-page-id]' }]
  },

  renderHTML({ HTMLAttributes, mark }) {
    return [
      'a',
      mergeAttributes(this.options.HTMLAttributes, HTMLAttributes, {
        href: `page://${String(mark.attrs.pageId)}`,
        class: 'rw-page-link',
      }),
      0,
    ]
  },

  addCommands() {
    return {
      setPageLink:
        (attrs: PageLinkAttributes) =>
        ({ commands }) =>
          commands.setMark(this.name, attrs),
      unsetPageLink:
        () =>
        ({ commands }) =>
          commands.unsetMark(this.name),
    }
  },
})
