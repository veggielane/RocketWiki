/**
 * Accessible naming for rendered mermaid diagrams.
 *
 * Mermaid has first-class alt-text directives — `accTitle: <text>` and
 * `accDescr: <text>` (or the multi-line `accDescr { … }`) — that live in
 * the diagram SOURCE, so they round-trip through Markdown for free (§4:
 * the fence body is the storage form). When either is present, mermaid
 * itself emits `<title>`/`<desc>` elements wired to the `<svg>` via
 * aria-labelledby/aria-describedby, and the preview surface must stay
 * role-less so that author-supplied name is what assistive tech reads —
 * `role="img"` on the wrapper would make the SVG a presentational child
 * and silence it. Only when the author supplied neither does the surface
 * fall back to `role="img"` with a label naming the diagram type: better
 * than an unlabeled graphic, worse than a real description.
 */

export function hasMermaidAccDirectives(source: string): boolean {
  // `accTitle :` / `accDescr {` spacing variants are accepted by mermaid's
  // grammar, so accept them here too.
  return /^\s*accTitle\s*:/m.test(source) || /^\s*accDescr\s*[:{]/m.test(source)
}

/**
 * Keyed by the first word of the diagram source, lowercased — how mermaid
 * itself dispatches to a diagram grammar. Unknown/new types read as the
 * generic "Mermaid diagram" rather than parroting a raw keyword like
 * "sequenceDiagram" at a screen reader.
 */
const DIAGRAM_TYPE_LABELS: Record<string, string> = {
  graph: 'Mermaid flowchart',
  flowchart: 'Mermaid flowchart',
  sequencediagram: 'Mermaid sequence diagram',
  classdiagram: 'Mermaid class diagram',
  statediagram: 'Mermaid state diagram',
  'statediagram-v2': 'Mermaid state diagram',
  erdiagram: 'Mermaid entity relationship diagram',
  gantt: 'Mermaid Gantt chart',
  pie: 'Mermaid pie chart',
  journey: 'Mermaid user journey',
  gitgraph: 'Mermaid git graph',
  mindmap: 'Mermaid mind map',
  timeline: 'Mermaid timeline',
  quadrantchart: 'Mermaid quadrant chart',
}

export function mermaidFallbackLabel(source: string): string {
  let inFrontmatter = false
  for (const rawLine of source.split('\n')) {
    const line = rawLine.trim()
    if (line.length === 0) continue
    // YAML frontmatter (--- … ---) and %% directives/comments may precede
    // the diagram keyword — skip them the way mermaid's detector does.
    if (line === '---') {
      inFrontmatter = !inFrontmatter
      continue
    }
    if (inFrontmatter || line.startsWith('%%')) continue
    const keyword = line.split(/[\s:]/, 1)[0].toLowerCase()
    return DIAGRAM_TYPE_LABELS[keyword] ?? 'Mermaid diagram'
  }
  return 'Mermaid diagram'
}
