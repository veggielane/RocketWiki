import manifest from './manifest.json'

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
 * These are the SAME files the public docs site serves as its "User guide"
 * (docs-site/scripts/generate.mjs re-emits `content/*.md` verbatim). The content
 * is authored once and served in both places; neither front-end owns a copy. The
 * shared `manifest.json` carries the only things that live outside the prose —
 * the section grouping, the order, and each topic's one-line summary — so the
 * in-app list and the site sidebar are built from one description, not two.
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

// The topic bodies, keyed by module path (`./content/<slug>.md`). A glob rather
// than one import per file so the manifest is the only place a topic is listed:
// adding a slug to the manifest and dropping in its `.md` is all it takes, and a
// mismatch surfaces as a thrown error at module load rather than a silent gap.
const bodies = import.meta.glob('./content/*.md', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>

function bodyFor(slug: string): string {
  const body = bodies[`./content/${slug}.md`]
  if (body === undefined) {
    throw new Error(`Help manifest lists "${slug}" but web/src/help/content/${slug}.md does not exist.`)
  }
  return body
}

// The title is the file's H1, never repeated in the manifest — the same rule the
// docs-site generator follows (it strips the H1 into Starlight's page title). The
// content test guarantees every file starts with "# ", so this cannot come back
// empty for real content.
function titleFrom(markdown: string, slug: string): string {
  const firstLine = markdown.trimStart().split('\n', 1)[0] ?? ''
  const match = firstLine.match(/^#\s+(.+?)\s*$/)
  if (!match) {
    throw new Error(`Help topic "${slug}" must start with an H1 "# Title"; got ${JSON.stringify(firstLine)}.`)
  }
  return match[1]
}

/**
 * Both sections are shown to everyone, on purpose. The admin topics describe how
 * grants, markings and audit WORK; they are not a list of who holds what, and
 * nothing in them is a secret — the same explanations sit in the repository's
 * design documents. Hiding them would mostly hide them from the space admin who
 * needed them, since instance-admin is not the only administering role.
 */
export const HELP_SECTIONS: HelpSection[] = manifest.sections.map((section) => ({
  title: section.title,
  topics: section.topics.map((topic) => {
    const markdown = bodyFor(topic.slug)
    return {
      slug: topic.slug,
      title: titleFrom(markdown, topic.slug),
      summary: topic.summary,
      markdown,
    }
  }),
}))

export const HELP_TOPICS: HelpTopic[] = HELP_SECTIONS.flatMap((s) => s.topics)

/**
 * Case-insensitive, like every other address in the app. These slugs are
 * resolved entirely client-side — there is no server lookup to be lenient on
 * this reader's behalf — so `/-/docs/Markings` would 404 while
 * `/spaces/ENG/My-Page` resolves, and the one URL scheme a lost reader is most
 * likely to have retyped by hand would be the strictest one in the product.
 */
export function findHelpTopic(slug: string | undefined): HelpTopic | undefined {
  if (slug === undefined) return undefined
  const wanted = slug.toLowerCase()
  return HELP_TOPICS.find((t) => t.slug.toLowerCase() === wanted)
}
