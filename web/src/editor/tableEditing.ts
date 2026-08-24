import { Extension } from '@tiptap/core'
import type { EditorState } from '@tiptap/pm/state'
import { CellSelection, isInTable, mergeCells, selectedRect, selectionCell } from '@tiptap/pm/tables'

export type TableColumnAlign = 'left' | 'center' | 'right' | null

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    tableEnhancements: {
      /**
       * Sets the GFM column alignment for every column the selection
       * touches — alignment is a per-COLUMN property in Markdown (the
       * delimiter row), so it is applied to every cell intersecting those
       * columns, spanning cells included. `null` clears back to the
       * unaligned `---` delimiter.
       */
      setTableColumnAlign: (align: TableColumnAlign) => ReturnType
      /**
       * `mergeCells`, but refusing merges that span the header/body
       * boundary: a header cell with a rowspan reaching into the body has
       * no Markdown representation (multimd's `^^` does not merge across
       * thead/tbody), so per design.md §4 the editor must not offer it.
       */
      mergeTableCells: () => ReturnType
    }
  }
}

/** True when the selection is a cell range covering the header row (row 0) AND at least one body row. */
export function mergeWouldCrossHeaderBoundary(state: EditorState): boolean {
  if (!(state.selection instanceof CellSelection)) return false
  const rect = selectedRect(state)
  return rect.top === 0 && rect.bottom > 1
}

/** The `align` attr of the cell around the selection (toolbar state), or null when not in a table. */
export function currentCellAlign(state: EditorState): TableColumnAlign {
  if (!isInTable(state)) return null
  const $cell = selectionCell(state)
  const align = $cell.nodeAfter?.attrs.align as unknown
  return align === 'left' || align === 'center' || align === 'right' ? align : null
}

/**
 * Editing behaviour for tables on top of TableKit. Commands and keymap
 * only — no schema, so the round-trip suite's schema and the live editor's
 * stay one and the same (extensions.ts).
 */
export const TableEnhancements = Extension.create({
  name: 'tableEnhancements',

  // Above default (100) so the Enter handler below runs before
  // StarterKit's paragraph-splitting Enter handlers.
  priority: 110,

  addCommands() {
    return {
      setTableColumnAlign:
        (align: TableColumnAlign) =>
        ({ state, dispatch }) => {
          if (!isInTable(state)) return false
          const rect = selectedRect(state)
          if (dispatch) {
            const { tr } = state
            const seen = new Set<number>()
            for (let row = 0; row < rect.map.height; row++) {
              for (let col = rect.left; col < rect.right; col++) {
                const cellPos = rect.map.map[row * rect.map.width + col]
                if (seen.has(cellPos)) continue
                seen.add(cellPos)
                const cell = rect.table.nodeAt(cellPos)
                if (!cell) continue
                if ((cell.attrs.align ?? null) !== align) {
                  tr.setNodeMarkup(rect.tableStart + cellPos, undefined, { ...cell.attrs, align })
                }
              }
            }
            dispatch(tr)
          }
          return true
        },

      mergeTableCells:
        () =>
        ({ state, dispatch }) => {
          if (!isInTable(state) || mergeWouldCrossHeaderBoundary(state)) return false
          return mergeCells(state, dispatch)
        },
    }
  },

  addKeyboardShortcuts() {
    return {
      // Inside a table cell, Enter inserts a line break (`<br>` in
      // Markdown) instead of splitting into a second paragraph — a cell is
      // a single paragraph in the §4 table model, and a break is what
      // actually round-trips. Matches Shift+Enter, and matches what the
      // next page load would have shown anyway. Everywhere else (including
      // a list pasted into a cell) Enter keeps its default behaviour.
      Enter: () => {
        const { $from } = this.editor.state.selection
        if ($from.depth < 2 || $from.parent.type.name !== 'paragraph') return false
        const container = $from.node($from.depth - 1)
        if (container.type.name !== 'tableCell' && container.type.name !== 'tableHeader') return false
        return this.editor.commands.setHardBreak()
      },
    }
  },
})
