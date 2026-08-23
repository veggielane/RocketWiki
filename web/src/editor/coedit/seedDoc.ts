import * as Y from 'yjs'
import { getSchema } from '@tiptap/core'
import type { Schema } from '@tiptap/pm/model'
import { prosemirrorJSONToYDoc } from '@tiptap/y-tiptap'
import { editorExtensions } from '../extensions'
import { markdownToJson } from '../markdown/fromMarkdown'

let cachedSchema: Schema | null = null

/**
 * The one ProseMirror schema of the app (design.md §4's "one renderer":
 * `editorExtensions` IS the schema — the rich editor only swaps rendering,
 * never node shapes), built once. Seeding must use this exact schema or
 * the seeder would push a document the joiners' editors can't hold.
 */
function editorSchema(): Schema {
  cachedSchema ??= getSchema(editorExtensions)
  return cachedSchema
}

/**
 * The seeder's half of the join contract (design.md §8, hub doc): convert
 * the page's saved Markdown through the ONE existing pipeline
 * (`markdownToJson`, the same function the solo editor loads through) into
 * the doc's `'default'` fragment — the fragment the TipTap Collaboration
 * extension binds by default. Round-trip fidelity of this conversion is
 * pinned by ydocRoundtrip.test.ts: Markdown → JSON → Y.Doc → JSON →
 * Markdown must stay byte-identical, or the first co-edit save would
 * rewrite content nobody touched.
 *
 * `origin` must be the provider (see SignalRYjsProviderOptions.seed): the
 * seed then travels as the provider's explicit full-state push instead of
 * being re-queued as an ordinary local edit.
 */
export function seedDocFromMarkdown(doc: Y.Doc, markdown: string, origin: unknown): void {
  const seeded = prosemirrorJSONToYDoc(editorSchema(), markdownToJson(markdown), 'default')
  Y.applyUpdate(doc, Y.encodeStateAsUpdate(seeded), origin)
  seeded.destroy()
}
