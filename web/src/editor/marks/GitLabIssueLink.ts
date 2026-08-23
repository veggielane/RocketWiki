import { Mark, mergeAttributes } from '@tiptap/core'

/**
 * `[text](gitlab-issue://{project}/{iid})` — a live GitLab issue link
 * (design.md §18). Same split as PageLink: its own mark rather than the
 * http(s)-only `link`, so the SPA can render it with a live open/closed
 * badge without touching the standard Link extension.
 *
 * This is the *schema-only* extension used by `editorExtensions` and the
 * headless round-trip suite. RichTextEditor swaps in
 * `GitLabIssueLinkWithView` (a React mark view that fetches live state),
 * exactly like DrawioDiagram → DrawioDiagramWithView — rendering only,
 * never serialization.
 *
 * Both attrs are raw strings (iid included): the round-trip rule (§4) is
 * byte-identity, and `Number()`-ing the iid would re-format the author's
 * digits.
 */
export interface GitLabIssueLinkAttributes {
  project: string
  iid: string
}

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    gitlabIssueLink: {
      setGitLabIssueLink: (attrs: GitLabIssueLinkAttributes) => ReturnType
      unsetGitLabIssueLink: () => ReturnType
    }
  }
}

export const GitLabIssueLink = Mark.create({
  name: 'gitlabIssueLink',

  inclusive: false,

  addAttributes() {
    return {
      project: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-gitlab-project'),
        renderHTML: (attributes: Record<string, unknown>) => ({
          'data-gitlab-project': attributes.project,
        }),
      },
      iid: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-gitlab-iid'),
        renderHTML: (attributes: Record<string, unknown>) => ({
          'data-gitlab-iid': attributes.iid,
        }),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'a[data-gitlab-project]' }]
  },

  renderHTML({ HTMLAttributes, mark }) {
    return [
      'a',
      mergeAttributes(HTMLAttributes, {
        href: `gitlab-issue://${String(mark.attrs.project)}/${String(mark.attrs.iid)}`,
        class: 'rw-gitlab-issue-link',
      }),
      0,
    ]
  },

  addCommands() {
    return {
      setGitLabIssueLink:
        (attrs: GitLabIssueLinkAttributes) =>
        ({ commands }) =>
          commands.setMark(this.name, attrs),
      unsetGitLabIssueLink:
        () =>
        ({ commands }) =>
          commands.unsetMark(this.name),
    }
  },
})
