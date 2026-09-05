import { describe, expect, it, vi } from 'vitest'
import { createRef } from 'react'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor, type RichTextEditorHandle } from '../RichTextEditor'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

vi.mock('../../pages/pageContext', () => ({ useCurrentPageId: () => null }))

/**
 * §2.1 and §2.2 — the two things design §4 lists as supported and the toolbar
 * could not do.
 *
 * Tables: the toolbar could insert a 2×2 and then merge, split and align its
 * cells, but there was NO add row, add column, delete row, delete column or
 * delete table anywhere in `web/src`. A column could never be added at all, and
 * a table inserted by mistake could only be removed by selecting across its
 * whole node — with Enter inside a cell remapped to a hard break, so the usual
 * escape hatch was gone too.
 *
 * Code blocks: `language` round-tripped through Markdown the whole time
 * (`fromMarkdown` reads the fence info, `toMarkdown` writes it back), but
 * nothing in the UI could reach the attribute. The only way to set one was the
 * stock CommonMark input rule — undiscoverable, and one-way.
 */

function renderEditor(initialMarkdown: string) {
  const handle = createRef<RichTextEditorHandle>()
  const mock = createMockUrqlClient((name) =>
    name === 'GitLabStatus' ? { gitlabStatus: { configured: false, baseUrl: null, viewerHasToken: false } } : undefined,
  )
  render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor ref={handle} initialMarkdown={initialMarkdown} />
    </UrqlProvider>,
  )
  return handle
}

/**
 * Confirms the caret is in the table, which is what reveals the table group.
 *
 * It already is: the fixture's first node IS the table, and TipTap's initial
 * selection is the start of the document. This used to click a cell to get
 * there, which in jsdom threw out of ProseMirror's `mousedown`
 * (`posAtCoords` → `document.elementFromPoint`, which jsdom does not
 * implement) — five unhandled exceptions per run, from a click that was
 * moving the caret to where it already was. Stubbing `elementFromPoint`
 * globally is NOT the fix: it hands axe's modal heuristic a layout-less DOM it
 * then answers wrongly, turning `aria-hidden-focus` from "incomplete" into a
 * false violation on every menu in the suite.
 */
function assertCaretInTable() {
  if (!document.querySelector('td')) throw new Error('no table cell rendered')
}

const TABLE = ['| A | B |', '| --- | --- |', '| 1 | 2 |', ''].join('\n')

describe('table row and column controls', () => {
  it('offers row, column and delete-table actions once the caret is in a table', async () => {
    renderEditor(TABLE)
    assertCaretInTable()

    expect(await screen.findByRole('button', { name: 'Row actions' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Column actions' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete table' })).toBeInTheDocument()
  })

  it('adds a row, and the new row survives the Markdown round trip', async () => {
    const handle = renderEditor(TABLE)
    assertCaretInTable()

    fireEvent.click(await screen.findByRole('button', { name: 'Row actions' }))
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Insert row below' }))

    // Three body-ish lines: header, delimiter, original row, new row.
    const lines = handle.current!.getMarkdown().trim().split('\n')
    expect(lines).toHaveLength(4)
    expect(lines[3]).toMatch(/^\|/)
  })

  it('adds a column — the growth path that did not exist at all', async () => {
    const handle = renderEditor(TABLE)
    assertCaretInTable()

    fireEvent.click(await screen.findByRole('button', { name: 'Column actions' }))
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Insert column right' }))

    // Every row gains a cell, so every line gains a pipe.
    for (const line of handle.current!.getMarkdown().trim().split('\n')) {
      expect(line.split('|').length).toBe(5)
    }
  })

  it('deletes the table, which previously had no control at all', async () => {
    const handle = renderEditor(TABLE)
    assertCaretInTable()

    fireEvent.click(await screen.findByRole('button', { name: 'Delete table' }))

    expect(handle.current!.getMarkdown().trim()).toBe('')
  })

  it('hides the whole group when the caret is not in a table', async () => {
    renderEditor('Just a paragraph.\n')
    await screen.findByRole('toolbar', { name: 'Formatting' })
    expect(screen.queryByRole('button', { name: 'Row actions' })).not.toBeInTheDocument()
  })

  it('has no axe violations with the row menu open', async () => {
    renderEditor(TABLE)
    assertCaretInTable()
    fireEvent.click(await screen.findByRole('button', { name: 'Row actions' }))
    await screen.findByRole('menu')
    await expectNoAxeViolations()
  })
})

/**
 * The code-block NodeView mounts through a React portal, so it is not in the
 * DOM on the synchronous render pass — every query here has to await it.
 */
describe('code block language', () => {
  const picker = () => screen.findByLabelText('Code language')

  it('shows the language a fence arrived with', async () => {
    renderEditor('```python\nx = 1\n```\n')
    expect(await picker()).toHaveValue('python')
  })

  it('shows Plain text for a fence with no language', async () => {
    renderEditor('```\nplain\n```\n')
    expect(await picker()).toHaveValue('')
  })

  it('sets a language, and it round-trips into the fence info string', async () => {
    const handle = renderEditor('```\nSELECT 1\n```\n')
    fireEvent.change(await picker(), { target: { value: 'sql' } })
    expect(handle.current!.getMarkdown()).toContain('```sql')
  })

  it('clears a language back to a bare fence', async () => {
    // The one-way trap: the input rule could set a language and nothing could
    // remove it.
    const handle = renderEditor('```python\nx = 1\n```\n')
    fireEvent.change(await picker(), { target: { value: '' } })
    const markdown = handle.current!.getMarkdown()
    expect(markdown).toContain('```\n')
    expect(markdown).not.toContain('```python')
  })

  it('offers no reserved fence language, which would produce a body the widget cannot parse', async () => {
    renderEditor('```\nx\n```\n')
    const options = Array.from((await picker()).querySelectorAll('option')).map((o) => (o as HTMLOptionElement).value)
    for (const reserved of ['mermaid', 'drawio', 'page-list', 'gitlab-file']) {
      expect(options).not.toContain(reserved)
    }
  })

  it('renders the language as a label, not a control, in read mode', async () => {
    // A braced expression, not a JSX string attribute: JSX string literals do
    // not process escapes, so `"...\n..."` would pass a literal backslash-n and
    // the fence would never parse.
    const { container } = render(
      <RichTextEditor initialMarkdown={'```python\nx = 1\n```\n'} editable={false} showToolbar={false} />,
    )
    // Queried by class rather than by text: the label is deliberately
    // `aria-hidden`, because the language is decoration beside a code block a
    // reader is already being read, not a second thing to announce.
    await waitFor(() => expect(container.querySelector('.rw-code-language-label')).not.toBeNull())
    expect(container.querySelector('.rw-code-language-label')).toHaveTextContent('python')
    expect(screen.queryByLabelText('Code language')).not.toBeInTheDocument()
  })
})
