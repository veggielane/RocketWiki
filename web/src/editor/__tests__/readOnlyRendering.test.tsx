import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { RichTextEditor } from '../RichTextEditor'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * design.md §4's "one renderer" means the SAME component renders a page for
 * reading and for editing. That makes the read-only path easy to forget, and
 * it is the most-read surface in the app: every page view, every rendered
 * comment, every help topic.
 *
 * `editorProps.attributes` are applied to the ProseMirror element whether or
 * not it is editable, so `role="textbox"` set unconditionally reached all of
 * them. A textbox role overrides its descendants' semantics: the headings,
 * lists, tables and links inside stop being structures a screen-reader user can
 * navigate by, and the whole article announces as one flat form field.
 *
 * axe passes it either way — `aria-multiline` is permitted on `textbox`, the
 * element is labelled, nothing is technically invalid — which is exactly why
 * this file exists rather than a line in the axe sweep.
 */

const MARKDOWN = `# Heading

Some text with a [link](https://example.com).

- first
- second
`

describe('RichTextEditor read-only rendering', () => {
  it('renders page content as a document, not as a text box', () => {
    render(<RichTextEditor initialMarkdown={MARKDOWN} editable={false} showToolbar={false} />)

    // The regression, stated directly: nothing on a read-only render may claim
    // to be an editable text field.
    expect(screen.queryByRole('textbox')).toBeNull()
  })

  it('keeps the document structure inside reachable', () => {
    render(<RichTextEditor initialMarkdown={MARKDOWN} editable={false} showToolbar={false} />)

    // These are exactly what a textbox role would have swallowed.
    expect(screen.getByRole('heading', { name: 'Heading' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'link' })).toBeInTheDocument()
    expect(screen.getByRole('list')).toBeInTheDocument()
  })

  it('carries a caller-supplied name on a landmark rather than on a text box', async () => {
    // A plain <div> cannot hold an accessible name (aria-label on a generic
    // role is ignored, and axe flags it), so a named read-only render becomes a
    // labelled region — which is a landmark a reader can jump to.
    render(
      <RichTextEditor
        initialMarkdown={MARKDOWN}
        editable={false}
        showToolbar={false}
        ariaLabel="Help: Protective markings"
      />,
    )

    expect(screen.getByRole('region', { name: 'Help: Protective markings' })).toBeInTheDocument()
    expect(screen.queryByRole('textbox')).toBeNull()
    await expectNoAxeViolations()
  })

  it('still presents the EDITING surface as a labelled multiline text box', () => {
    // The other half of the contract: the role belongs on the editable
    // surface, where a screen reader does need to be told it can type.
    render(<RichTextEditor initialMarkdown={MARKDOWN} editable showToolbar={false} />)

    const box = screen.getByRole('textbox', { name: 'Page content' })
    expect(box).toHaveAttribute('aria-multiline', 'true')
  })
})
