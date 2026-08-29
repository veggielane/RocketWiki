import gettingStarted from './content/getting-started.md?raw'
import classification from './content/classification.md?raw'
import organising from './content/organising.md?raw'
import watching from './content/watching-and-notifications.md?raw'
import adminSpaces from './content/admin-spaces.md?raw'
import adminInstance from './content/admin-instance.md?raw'

/**
 * The help topics, as Markdown imported at build time.
 *
 * Markdown files rather than JSX, for the same reason the wiki stores Markdown:
 * prose belongs in a format a person can edit without reading React. `?raw` means
 * they are bundled as strings and rendered through the app's own read-only
 * renderer, so help pages get the same headings, tables and code blocks that a
 * wiki page does — one renderer, not a second Markdown path that could drift
 * (design.md §4).
 *
 * Deliberately NOT stored as wiki pages. Help has to work on an instance with no
 * content, for a reader who has not been granted a role in anything yet — which
 * is exactly the reader most likely to need it.
 */
export interface HelpTopic {
  slug: string
  title: string
  /** One line, shown in the topic list. */
  summary: string
  markdown: string
}

export interface HelpSection {
  title: string
  topics: HelpTopic[]
}

/**
 * Both sections are shown to everyone, on purpose. The admin topics describe how
 * grants, markings and audit WORK; they are not a list of who holds what, and
 * nothing in them is a secret — the same explanations sit in the repository's
 * design documents. Hiding them would mostly hide them from the space admin who
 * needed them, since instance-admin is not the only administering role.
 */
export const HELP_SECTIONS: HelpSection[] = [
  {
    title: 'Using RocketWiki',
    topics: [
      {
        slug: 'getting-started',
        title: 'Getting started',
        summary: 'Spaces, pages, writing and finding things.',
        markdown: gettingStarted,
      },
      {
        slug: 'classification',
        title: 'Classifications and markings',
        summary: 'What the banner means, and why you may see less than a colleague.',
        markdown: classification,
      },
      {
        slug: 'organising',
        title: 'Organising a space',
        summary: 'The tree, labels, icons, properties, forms and trash.',
        markdown: organising,
      },
      {
        slug: 'watching-and-notifications',
        title: 'Watching, comments and notifications',
        summary: 'Following pages and being told when they change.',
        markdown: watching,
      },
    ],
  },
  {
    title: 'Administering RocketWiki',
    topics: [
      {
        slug: 'admin-spaces',
        title: 'Administering a space',
        summary: 'Settings, grants, restrictions, trash and analytics.',
        markdown: adminSpaces,
      },
      {
        slug: 'admin-instance',
        title: 'Administering the instance',
        summary: 'Analytics, audit, vocabularies, sync — and what admin does not grant.',
        markdown: adminInstance,
      },
    ],
  },
]

export const HELP_TOPICS: HelpTopic[] = HELP_SECTIONS.flatMap((s) => s.topics)

export function findHelpTopic(slug: string | undefined): HelpTopic | undefined {
  return HELP_TOPICS.find((t) => t.slug === slug)
}
