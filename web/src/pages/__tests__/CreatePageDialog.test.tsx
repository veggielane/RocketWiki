import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { CreatePageDialog } from '../CreatePageDialog'
import { expectNoAxeViolations } from '../../test/axe'

function open(overrides: Partial<React.ComponentProps<typeof CreatePageDialog>> = {}) {
  const onConfirm = vi.fn()
  const onCancel = vi.fn()
  render(
    <CreatePageDialog
      open
      parentLabel="Engineering"
      onCancel={onCancel}
      onConfirm={onConfirm}
      {...overrides}
    />,
  )
  return { onConfirm, onCancel }
}

const titleBox = () => screen.getByLabelText('Title')
const slugBox = () => screen.getByLabelText('URL slug') as HTMLInputElement

describe('CreatePageDialog', () => {
  it('names what the page will be created under', () => {
    open()
    expect(screen.getByText('New page in Engineering')).toBeInTheDocument()
  })

  it('derives the slug from the title as you type', () => {
    open()
    fireEvent.change(titleBox(), { target: { value: 'Static Fire Notes' } })
    expect(slugBox().value).toBe('static-fire-notes')
  })

  it('folds a hand-typed slug to lower case in the field itself', () => {
    // The server stores slugs lowercased, so a field reading "My-Page" while the
    // page is created at "my-page" would be the UI promising an address the
    // server does not keep — first noticed in the URL bar after Create.
    open()
    fireEvent.change(slugBox(), { target: { value: 'SF-2026' } })
    expect(slugBox().value).toBe('sf-2026')
  })

  it('folds as you type without eating characters mid-word', () => {
    // Case-folding only. A full re-slugify per keystroke would trim the trailing
    // hyphen and "post-mortem" could never be typed one character at a time.
    open()
    fireEvent.change(slugBox(), { target: { value: 'Post-' } })
    expect(slugBox().value).toBe('post-')
    fireEvent.change(slugBox(), { target: { value: 'post-Mortem' } })
    expect(slugBox().value).toBe('post-mortem')
  })

  it('submits the folded slug, not the typed one', () => {
    const onConfirm = vi.fn()
    open({ onConfirm })
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Static fire' } })
    fireEvent.change(slugBox(), { target: { value: 'SF-2026' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(onConfirm).toHaveBeenCalledWith({
      title: 'Static fire',
      slug: 'sf-2026',
      parentPageId: null,
      icon: null,
    })
  })

  it('stops tracking the title once the slug is edited by hand', () => {
    // A slug is part of the page's URL — silently rewriting one someone set
    // deliberately would be the wrong kind of helpful.
    open()
    fireEvent.change(titleBox(), { target: { value: 'Static Fire' } })
    fireEvent.change(slugBox(), { target: { value: 'sf-2026' } })
    fireEvent.change(titleBox(), { target: { value: 'Static Fire Rescheduled' } })
    expect(slugBox().value).toBe('sf-2026')
  })

  it('submits the trimmed title and the effective slug', () => {
    const { onConfirm } = open()
    fireEvent.change(titleBox(), { target: { value: '  Launch notes  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(onConfirm).toHaveBeenCalledWith({
      title: 'Launch notes',
      slug: 'launch-notes',
      parentPageId: null,
      icon: null,
    })
  })

  it('cannot be submitted with no title', () => {
    open()
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled()
  })

  it('refuses a title that slugifies to nothing, and says what to do', () => {
    open()
    fireEvent.change(titleBox(), { target: { value: '???' } })
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled()
    expect(screen.getByText(/no characters a URL can use/)).toBeInTheDocument()
  })

  it('lets that title through once a slug is typed by hand', () => {
    const { onConfirm } = open()
    fireEvent.change(titleBox(), { target: { value: '???' } })
    fireEvent.change(slugBox(), { target: { value: 'mystery' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(onConfirm).toHaveBeenCalledWith({ title: '???', slug: 'mystery', parentPageId: null, icon: null })
  })

  it('creates with no icon unless one is picked', () => {
    const { onConfirm } = open()
    fireEvent.change(titleBox(), { target: { value: 'Launch notes' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(onConfirm).toHaveBeenCalledWith(expect.objectContaining({ icon: null }))
  })

  it('carries a picked icon into the create', () => {
    const { onConfirm } = open()
    fireEvent.change(titleBox(), { target: { value: 'Launch notes' } })
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Icon' }))
    fireEvent.click(screen.getByRole('option', { name: 'Rocket' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(onConfirm).toHaveBeenCalledWith(expect.objectContaining({ icon: 'ROCKET' }))
  })

  it('shows a server refusal inline and keeps what was typed', () => {
    open({ error: 'A page with that slug already exists in this space.' })
    fireEvent.change(titleBox(), { target: { value: 'Launch notes' } })
    expect(screen.getByText('A page with that slug already exists in this space.')).toBeInTheDocument()
    expect(slugBox().value).toBe('launch-notes')
  })

  it('does not submit twice while a create is in flight', () => {
    const { onConfirm } = open({ busy: true })
    fireEvent.change(titleBox(), { target: { value: 'Launch notes' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    expect(onConfirm).not.toHaveBeenCalled()
  })

  it('has no axe violations open, with every field and a refusal showing', async () => {
    // The policy is "every dialog passes axe in its OPEN state"
    // (docs/ACCESSIBILITY.md); it lives here rather than in
    // test/dialogsA11y.test.tsx because that suite is for the dialogs with no
    // behaviour test file of their own.
    open({
      error: 'A page with that slug already exists in this space.',
      parentOptions: [
        { id: null, title: 'Engineering', depth: 0 },
        { id: 'p1', title: 'Launch notes', depth: 1 },
      ],
    })
    screen.getByRole('dialog')
    await expectNoAxeViolations()
  })

  it('clears its fields on cancel, so reopening starts fresh', () => {
    const { onCancel } = open()
    fireEvent.change(titleBox(), { target: { value: 'Discarded' } })
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Icon' }))
    fireEvent.click(screen.getByRole('option', { name: 'Rocket' }))
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onCancel).toHaveBeenCalled()
    expect(titleBox()).toHaveValue('')
    expect(screen.getByRole('combobox', { name: 'Icon' })).toHaveTextContent('No icon')
  })
})
