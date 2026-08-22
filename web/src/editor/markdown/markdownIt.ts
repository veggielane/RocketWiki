import MarkdownIt from 'markdown-it'
import container from 'markdown-it-container'
import { CALLOUT_TYPES } from '../nodes/Callout'
import { mentionRule } from './mentionRule'
import { taskListRule } from './taskListRule'

export function createMarkdownIt(): InstanceType<typeof MarkdownIt> {
  const md = new MarkdownIt('default', {
    html: false, // no raw HTML passthrough — outside the v1 feature set and a sanitization risk
    linkify: false,
    breaks: false,
  })

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
