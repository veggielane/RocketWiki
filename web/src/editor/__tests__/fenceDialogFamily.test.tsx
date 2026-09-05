import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { InsertGitLabFileDialog } from '../gitlab/InsertGitLabFileDialog'
import { InsertGitLabIssueLinkDialog } from '../gitlab/InsertGitLabIssueLinkDialog'
import { InsertGitLabIssuesDialog } from '../gitlab/InsertGitLabIssuesDialog'
import { DrawioEditorDialog } from '../drawio/DrawioEditorDialog'
import { CreatePageDialog } from '../../pages/CreatePageDialog'

/**
 * The insert dialogs reached from the editor toolbar, tested as the FAMILY they
 * are rather than one at a time — because every defect here was a divergence
 * between siblings, not a bug inside one of them. Each `it` below fails on the
 * code as it stood before this pass.
 */

const DRAWIO_CONFIG = { url: 'https://drawio.internal.test/', publicService: false }

/** Mouse-down then click on the container, which is how MUI recognises a backdrop click. */
function clickBackdrop(): void {
  const container = document.querySelector('.MuiDialog-container')
  if (!container) throw new Error('no dialog container')
  fireEvent.mouseDown(container)
  fireEvent.click(container)
}

describe('a backdrop click cannot discard typed work', () => {
  it('keeps the draw.io dialog open — the iframe holds the only copy of the diagram', () => {
    const onClose = vi.fn()
    render(
      <DrawioEditorDialog open payload="" alt="" onSave={vi.fn()} onClose={onClose} config={DRAWIO_CONFIG} />,
    )

    clickBackdrop()

    expect(onClose).not.toHaveBeenCalled()
  })
})

describe('every insert dialog opens with focus in a field', () => {
  // A dialog that opens with focus on its own paper makes a keyboard user Tab
  // in before they can type — and four of six did exactly that. The page-list
  // dialog's own case lives in insertPageListDialog.test.tsx, which has the
  // urql provider its vocabulary query needs.
  it('gitlab-file focuses Project', () => {
    render(<InsertGitLabFileDialog open onClose={vi.fn()} onInsert={vi.fn()} />)
    expect(screen.getByLabelText(/^Project/)).toHaveFocus()
  })

  it('gitlab-issues focuses Project', () => {
    render(<InsertGitLabIssuesDialog open onClose={vi.fn()} onInsert={vi.fn()} />)
    expect(screen.getByLabelText(/^Project/)).toHaveFocus()
  })

  it('gitlab-issue-link focuses the URL field it wants pasted into', () => {
    render(<InsertGitLabIssueLinkDialog open initialText="" onClose={vi.fn()} onInsert={vi.fn()} />)
    expect(screen.getByLabelText(/Paste a GitLab issue URL/)).toHaveFocus()
  })
})

describe('a disabled Insert says what it is waiting for', () => {
  it('names both missing fields in the gitlab-file dialog, then the one that is left', () => {
    render(<InsertGitLabFileDialog open onClose={vi.fn()} onInsert={vi.fn()} />)

    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
    expect(screen.getByText('Insert needs a project and a file path.')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText(/^Project/), { target: { value: 'propulsion/turbopump' } })

    expect(screen.getByText('Insert needs a file path.')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText(/File path/), { target: { value: 'docs/spec.md' } })

    expect(screen.queryByText(/^Insert needs/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Insert' })).toBeEnabled()
  })

  it('names the missing project in the gitlab-issues dialog', () => {
    render(<InsertGitLabIssuesDialog open onClose={vi.fn()} onInsert={vi.fn()} />)
    expect(screen.getByText('Insert needs a project.')).toBeInTheDocument()
  })

  it('distinguishes a missing issue number from a non-numeric one', () => {
    render(<InsertGitLabIssueLinkDialog open initialText="" onClose={vi.fn()} onInsert={vi.fn()} />)

    fireEvent.change(screen.getByLabelText(/^Project/), { target: { value: 'propulsion/turbopump' } })
    expect(screen.getByText('Insert needs an issue number.')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText(/Issue number/), { target: { value: 'forty-two' } })
    expect(screen.getByText('Insert needs a numeric issue number.')).toBeInTheDocument()
  })

  it('announces the requirement politely rather than interrupting mid-word', () => {
    const { container } = render(<InsertGitLabFileDialog open onClose={vi.fn()} onInsert={vi.fn()} />)
    const live = container.ownerDocument.querySelector('[aria-live="polite"]')
    expect(live).not.toBeNull()
    expect(within(live as HTMLElement).getByText(/^Insert needs/)).toBeInTheDocument()
  })
})

describe('reopening a dialog gives a fresh form', () => {
  it('clears the create-page dialog after a successful create', () => {
    // The real bug: both callers keep this mounted and the success path closes
    // it without going through Cancel, so the next "New page" opened onto the
    // last page's title and slug.
    const props = { parentLabel: 'Propulsion', onCancel: vi.fn(), onConfirm: vi.fn() }
    const { rerender } = render(<CreatePageDialog open {...props} />)

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Ignition Report' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(props.onConfirm).toHaveBeenCalled()

    // The caller closes on success — no Cancel involved.
    rerender(<CreatePageDialog open={false} {...props} />)
    rerender(<CreatePageDialog open {...props} />)

    expect(screen.getByLabelText('Title')).toHaveValue('')
    expect(screen.getByLabelText('URL slug')).toHaveValue('')
  })

  it('re-seeds the draw.io alt field from the diagram being opened', () => {
    const props = { payload: '', onSave: vi.fn(), onClose: vi.fn(), config: DRAWIO_CONFIG }
    const { rerender } = render(<DrawioEditorDialog open alt="first diagram" {...props} />)

    rerender(<DrawioEditorDialog open={false} alt="first diagram" {...props} />)
    rerender(<DrawioEditorDialog open alt="second diagram" {...props} />)

    expect(screen.getByLabelText(/Diagram description/)).toHaveValue('second diagram')
  })
})

describe('the family reads as one family', () => {
  it('says "Insert", never "Embed", over a button that says Insert', () => {
    render(<InsertGitLabFileDialog open onClose={vi.fn()} onInsert={vi.fn()} />)
    expect(screen.getByRole('heading', { name: 'Insert GitLab file' })).toBeInTheDocument()
  })

  it('keeps a helper line under the issue number so the fields below do not jump', () => {
    render(<InsertGitLabIssueLinkDialog open initialText="" onClose={vi.fn()} onInsert={vi.fn()} />)
    const field = screen.getByLabelText(/Issue number/)

    const helperWhenEmpty = document.getElementById(field.getAttribute('aria-describedby') ?? '')
    expect(helperWhenEmpty?.textContent).toBeTruthy()

    fireEvent.change(field, { target: { value: '42' } })

    const helperWhenValid = document.getElementById(field.getAttribute('aria-describedby') ?? '')
    expect(helperWhenValid?.textContent).toBeTruthy()
  })
})
