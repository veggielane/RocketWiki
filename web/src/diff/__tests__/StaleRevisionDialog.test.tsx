import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { expectNoAxeViolations } from '../../test/axe'
import { StaleRevisionDialog, type StaleRevisionDialogProps } from '../StaleRevisionDialog'

function renderDialog(overrides: Partial<StaleRevisionDialogProps> = {}) {
  const handlers = {
    onOverwriteAnyway: vi.fn(),
    onCopyAndCancel: vi.fn(),
    onKeepEditing: vi.fn(),
  }
  render(
    <StaleRevisionDialog
      open
      currentRevisionNumber={7}
      yourTitle="Runbook"
      yourDraft={'shared line\nmy line\n'}
      theirTitle={null}
      theirContent={'shared line\ntheir line\n'}
      {...handlers}
      {...overrides}
    />,
  )
  return handlers
}

describe('StaleRevisionDialog', () => {
  it('shows the diff by default — the evidence is the dialog body, not behind a click', () => {
    renderDialog()

    const diff = screen.getByRole('region', { name: 'Their changes compared with your draft' })
    expect(diff).toHaveTextContent('shared line')
    expect(diff).toHaveTextContent('my line')
    expect(diff).toHaveTextContent('their line')
    // Page content stays text: the raw Markdown appears verbatim, never parsed.
    expect(screen.getByRole('dialog')).toHaveTextContent('revision 7')
  })

  it('renders their Markdown as text even when it looks like HTML', () => {
    renderDialog({ theirContent: '<img src=x onerror=alert(1)>\n' })
    // The literal characters are visible — nothing was interpreted as markup.
    expect(screen.getByRole('region', { name: 'Their changes compared with your draft' })).toHaveTextContent(
      '<img src=x onerror=alert(1)>',
    )
    expect(document.querySelector('img')).toBeNull()
  })

  it('marks the differing words inside a changed line', () => {
    renderDialog({ yourDraft: 'The quick brown fox.\n', theirContent: 'The slow brown fox.\n' })
    const marks = Array.from(document.querySelectorAll('mark')).map((m) => m.textContent)
    expect(marks).toEqual(['quick', 'slow'])
  })

  it('shows both titles when they differ', () => {
    renderDialog({ theirTitle: 'Runbook v2' })
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveTextContent('Their title: Runbook v2')
    expect(dialog).toHaveTextContent('Your title: Runbook')
  })

  it('omits the title comparison when their title matches or is absent', () => {
    renderDialog({ theirTitle: 'Runbook' })
    expect(screen.getByRole('dialog')).not.toHaveTextContent('Their title:')
  })

  it('says so plainly when there is nothing to compare', () => {
    renderDialog({ yourDraft: 'same\n', theirContent: 'same\n' })
    // Worded for the comparison rather than for this dialog: the same view now
    // also diffs two saved revisions, where neither side is anyone's draft.
    expect(screen.getByRole('dialog')).toHaveTextContent('The two versions are identical.')
    expect(screen.queryByRole('region', { name: 'Their changes compared with your draft' })).toBeNull()
  })

  it('degrades to an explanation when the error carried no content', () => {
    renderDialog({ theirContent: null })
    expect(screen.getByRole('dialog')).toHaveTextContent("Their latest content isn't available to compare.")
    expect(screen.queryByRole('region', { name: 'Their changes compared with your draft' })).toBeNull()
  })

  it('offers the three-way flow, with the non-destructive option as default focus', () => {
    const handlers = renderDialog()

    const keepEditing = screen.getByRole('button', { name: 'Keep editing' })
    expect(keepEditing).toHaveFocus()

    fireEvent.click(keepEditing)
    fireEvent.click(screen.getByRole('button', { name: 'Overwrite anyway' }))
    fireEvent.click(screen.getByRole('button', { name: 'Copy my text and cancel' }))
    expect(handlers.onKeepEditing).toHaveBeenCalledTimes(1)
    expect(handlers.onOverwriteAnyway).toHaveBeenCalledTimes(1)
    expect(handlers.onCopyAndCancel).toHaveBeenCalledTimes(1)
  })

  it('has no axe violations with the diff open', async () => {
    renderDialog({ theirTitle: 'Runbook v2' })
    // Shared WCAG 2.2 AA policy + documented jsdom exclusions (test/axe.ts);
    // the browser layer (web/a11y) re-checks this dialog with contrast on.
    await expectNoAxeViolations()
  })
})
