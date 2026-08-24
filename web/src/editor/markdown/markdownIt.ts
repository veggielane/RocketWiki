import MarkdownIt from 'markdown-it'
import container from 'markdown-it-container'
import multimdTable from 'markdown-it-multimd-table'
import { CALLOUT_TYPES } from '../nodes/Callout'
import { mentionRule } from './mentionRule'
import { taskListRule } from './taskListRule'

export function createMarkdownIt(): InstanceType<typeof MarkdownIt> {
  const md = new MarkdownIt('default', {
    html: false, // no raw HTML passthrough — outside the v1 feature set and a sanitization risk
    linkify: false,
    breaks: false,
  })

  // Tables: markdown-it-multimd-table REPLACES the core GFM table rule
  // (`md.block.ruler.at('table', …)`), adding column spans (`| wide || x |`)
  // and — with `rowspan: true` — row spans (`| ^^ |`), while still parsing
  // plain GFM pipe tables to the exact same token-stream shape the core rule
  // produced (verified empirically; the corpus round-trip suite enforces it
  // stays that way). It is a *block*-rule swap only: non-table content never
  // reaches it.
  //
  // Options, deliberately minimal:
  // - multiline: false — cell newlines are literal `<br>` (the
  //   GFM-ubiquitous convention that also renders on GitHub), not multimd's
  //   trailing-`\` line continuation, so the continuation syntax stays off.
  // - rowspan: true — `^^` merges a cell with the one above it.
  // - headerless: false — GFM and the editor's table model both require a
  //   header row.
  // - multibody: false — a blank line ends the table (GFM behaviour); no
  //   multi-<tbody> sections, which the editor cannot represent.
  // - autolabel: false — no auto-generated caption ids (captions themselves
  //   are unsupported and rejected in fromMarkdown.ts).
  //
  // Compat shim: markdown-it v15 removed `md.utils.assign`, which the
  // plugin (last released against v14) calls exactly once, at install time,
  // to merge its options. `Object.assign` is a drop-in superset. `md.utils`
  // is shared module state, so the shim is scoped to the `.use()` call and
  // removed again rather than left as a permanent global mutation.
  const utils = md.utils as typeof md.utils & { assign?: typeof Object.assign }
  const hadAssign = typeof utils.assign === 'function'
  if (!hadAssign) utils.assign = Object.assign
  try {
    md.use(multimdTable, {
      multiline: false,
      rowspan: true,
      headerless: false,
      multibody: false,
      autolabel: false,
    })
  } finally {
    if (!hadAssign) delete utils.assign
  }

  for (const type of CALLOUT_TYPES) {
    // @types/markdown-it-container declares the options arg as required;
    // the actual runtime defaults it (`options = options || {}`) — passing
    // `{}` satisfies the (slightly wrong) type without changing behavior.
    md.use(container, type, {})
  }

  md.inline.ruler.before('link', 'mention', mentionRule)
  md.core.ruler.push('task_lists', taskListRule)

  return md
}
