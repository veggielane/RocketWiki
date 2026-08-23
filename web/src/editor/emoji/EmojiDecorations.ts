import { Extension } from '@tiptap/core'
import { Plugin, PluginKey, type EditorState, type Transaction } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { findEmojiMatches } from '../../emoji/findEmojiMatches'
import { getEmojiRegistry, subscribeEmojiRegistry } from '../../emoji/registry'
import { getEmojiUrl, peekEmojiUrl } from '../../emoji/emojiBlobCache'

/**
 * Renders registered `:name:` occurrences as inline images in **read
 * mode** — page view and comment bodies — via ProseMirror *decorations*,
 * deliberately not a schema node (the mermaid/draw.io precedent: rendering
 * concerns never touch the schema). The document only ever contains the
 * literal text, so Markdown → editor → Markdown is untouched by
 * construction: there is nothing to serialize, and the round-trip suite
 * (which uses the plain extension list) never sees this extension at all.
 *
 * Read mode only, decided rather than defaulted: while *editing*, the
 * literal `:name:` is the source of truth the author is manipulating —
 * hiding it under an image would fight the cursor. Authoring gets the
 * `:` autocomplete and the toolbar picker instead (EmojiSuggestion.ts),
 * both of which insert plain text.
 *
 * Rendering rule (design.md §19): candidates between colons, filtered
 * against the fetched registry — an unknown name stays literal text, no
 * error UI. Images come from the (name, etag) blob cache
 * (emoji/emojiBlobCache.ts) over the authenticated route; until a blob
 * has resolved the literal text stays visible (content is never blanked),
 * and the plugin re-decorates when the load settles or the registry
 * changes.
 */

export const emojiDecorationsKey = new PluginKey<DecorationSet>('rwEmojiDecorations')

const REFRESH = 'refresh'

function createEmojiWidget(name: string, url: string): HTMLElement {
  const img = document.createElement('img')
  img.className = 'rw-emoji'
  img.src = url
  img.alt = `:${name}:`
  img.title = `:${name}:`
  img.draggable = false
  return img
}

export const EmojiDecorations = Extension.create({
  name: 'rwEmojiDecorations',

  addProseMirrorPlugins() {
    const editor = this.editor
    // Set once the plugin view exists; blob loads settle on later microtasks,
    // so this is always assigned before the first `then` fires. Cleared on
    // destroy so a late resolution never touches a dead view.
    let requestRefresh: (() => void) | null = null

    const buildDecorations = (state: EditorState): DecorationSet => {
      if (editor.isEditable) {
        return DecorationSet.empty
      }
      const registry = getEmojiRegistry()
      if (registry.size === 0) {
        return DecorationSet.empty
      }
      const decorations: Decoration[] = []
      state.doc.descendants((node, pos) => {
        if (!node.isText || !node.text) return true
        for (const match of findEmojiMatches(node.text, (name) => registry.has(name))) {
          const etag = registry.get(match.name)
          if (etag === undefined) continue
          const url = peekEmojiUrl(match.name, etag)
          if (url === undefined) {
            // Not resolved yet: leave the literal text visible, load in the
            // background, re-decorate when it settles.
            void getEmojiUrl(match.name, etag).then(() => requestRefresh?.())
            continue
          }
          if (url === null) continue // settled miss — literal text stays
          const from = pos + match.from
          const to = pos + match.to
          decorations.push(Decoration.inline(from, to, { class: 'rw-emoji-source-hidden' }))
          decorations.push(
            Decoration.widget(from, () => createEmojiWidget(match.name, url), {
              key: `rw-emoji-${match.name}-${etag}`,
              side: 0,
            }),
          )
        }
        return true
      })
      return DecorationSet.create(state.doc, decorations)
    }

    return [
      new Plugin<DecorationSet>({
        key: emojiDecorationsKey,
        state: {
          init: (_config, state) => buildDecorations(state),
          apply: (tr: Transaction, old: DecorationSet, _oldState, newState) => {
            if (tr.docChanged || tr.getMeta(emojiDecorationsKey) === REFRESH) {
              return buildDecorations(newState)
            }
            return old.map(tr.mapping, tr.doc)
          },
        },
        props: {
          decorations(state) {
            return emojiDecorationsKey.getState(state)
          },
        },
        view: (editorView) => {
          requestRefresh = () => {
            editorView.dispatch(editorView.state.tr.setMeta(emojiDecorationsKey, REFRESH))
          }
          // Registry changes (initial load, admin add/remove) re-decorate
          // live documents. Torn down with the view — a leaked subscription
          // would dispatch into a destroyed editor.
          const unsubscribe = subscribeEmojiRegistry(() => requestRefresh?.())
          return {
            destroy: () => {
              unsubscribe()
              requestRefresh = null
            },
          }
        },
      }),
    ]
  },
})
