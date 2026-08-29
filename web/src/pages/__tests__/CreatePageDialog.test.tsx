import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { CreatePageDialog } from '../CreatePageDialog'

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
    expect(onConfirm).toHaveBeenCalledWith({ title: 'Launch notes', slug: 'launch-notes' })
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
    expect(onConfirm).toHaveBeenCalledWith({ title: '???', slug: 'mystery' })
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

  it('clears its fields on cancel, so reopening starts fresh', () => {
    const { onCancel } = open()
    fireEvent.change(titleBox(), { target: { value: 'Discarded' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onCancel).toHaveBeenCalled()
    expect(titleBox()).toHaveValue('')
  })
})
