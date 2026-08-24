/**
 * markdown-it-multimd-table ships no TypeScript types and DefinitelyTyped
 * has none (checked 2026-08: `@types/markdown-it-multimd-table` is a 404),
 * so the surface we use is declared here. Options mirror the plugin's
 * documented defaults; see markdownIt.ts for which ones we enable and why.
 */
declare module 'markdown-it-multimd-table' {
  import type MarkdownIt from 'markdown-it'

  interface MultimdTableOptions {
    /** `\` at end-of-line merges the next source line into the row. Off — cell newlines are `<br>` (design.md §4). */
    multiline?: boolean
    /** A cell containing exactly `^^` merges with the cell above. */
    rowspan?: boolean
    /** Allow tables with no header row. Off — GFM (and the editor's table model) requires one. */
    headerless?: boolean
    /** Blank-ish lines split the table into multiple `<tbody>`s. Off — one thead + one tbody. */
    multibody?: boolean
    /** Auto-generate `id` attributes for table captions. Off. */
    autolabel?: boolean
  }

  function multimdTable(md: MarkdownIt, options?: MultimdTableOptions): void

  export default multimdTable
}
