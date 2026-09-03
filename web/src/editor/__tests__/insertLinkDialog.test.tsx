import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { InsertLinkDialog, type LinkTarget } from '../InsertLinkDialog'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

vi.mock('../../pages/pageContext', () => ({ useCurrentPageId: () => 'page-1' }))

/**
 * Links used to be made with `window.prompt('Link URL (https://…)')` — the most
 * used action in the editor, and the only affordance in the app that was not a
 * real dialog. It could not reach `page://` at all, so design.md §4's
 * rename-stable internal link was unreachable from the UI; it could not edit or
 * remove an existing link; and it validated nothing.
 */

const treeNode = (id: string, title: string, children: unknown[] = []) => ({
  id,
  title,
  icon: null,
  slug: title.toLowerCase().replace(/\s+/g, '-'),
  hasChildren: children.length > 0,
  sortOrder: 0,
  hasRestrictions: false,
  labels: [],
  marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
  children,
})

/** A page the author cannot read, at its sibling position (design.md §6.7 / §21.8): no id, so nothing to link to. */
const protectedLeaf = {
  __typename: 'ProtectedTreeNode',
  title: '(protected)',
  sortOrder: 1,
  denial: { placeholderTitle: '(protected)', noSpaceAccess: false, marking: null, reasons: [] },
}

function renderDialog({
  existing = null as LinkTarget | null,
  initialText = '',
  onSubmit = vi.fn(),
  onRemove = undefined as (() => void) | undefined,
  pageTree = [treeNode('page-2', 'Ignition Report'), protectedLeaf] as unknown[],
} = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageSpaceRef') return { page: { id: 'page-1', spaceId: 'space-1', spaceKey: 'PROP' } }
    if (name === 'SpacePageTree') return { pageTree }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <InsertLinkDialog
        open
        initialText={initialText}
        existing={existing}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        onRemove={onRemove}
      />
    </UrqlProvider>,
  )
  return { onSubmit }
}

describe('InsertLinkDialog', () => {
  it('inserts an ordinary web link', async () => {
    const { onSubmit } = renderDialog({ initialText: 'the runbook' })

    fireEvent.change(screen.getByLabelText('Web address'), { target: { value: 'https://example.com/runbook' } })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    expect(onSubmit).toHaveBeenCalledWith({ kind: 'external', href: 'https://example.com/runbook' }, 'the runbook')
  })

  it('refuses a target that is not an http(s) address', () => {
    // The prompt accepted anything typed into it, including a `javascript:`
    // URL, which the link mark must never carry.
    renderDialog({ initialText: 'click me' })

    fireEvent.change(screen.getByLabelText('Web address'), { target: { value: 'javascript:alert(1)' } })
    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
    expect(screen.getByText('Needs a full http:// or https:// address.')).toBeInTheDocument()
  })

  it('links to a wiki page by id, which is what survives a rename (design.md §4)', async () => {
    const { onSubmit } = renderDialog({ initialText: 'the anomaly' })

    fireEvent.click(screen.getByRole('button', { name: 'Wiki page' }))
    const picker = await screen.findByLabelText('Page')
    fireEvent.mouseDown(picker)
    fireEvent.click(await screen.findByRole('option', { name: 'Ignition Report' }))
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    expect(onSubmit).toHaveBeenCalledWith({ kind: 'page', pageId: 'page-2' }, 'the anomaly')
  })

  it('offers no placeholder for a page the author cannot read — there is no id to link to', async () => {
    renderDialog({ initialText: 'x' })
    fireEvent.click(screen.getByRole('button', { name: 'Wiki page' }))
    const picker = await screen.findByLabelText('Page')
    fireEvent.mouseDown(picker)
    expect(await screen.findByRole('option', { name: 'Ignition Report' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: '(protected)' })).toBeNull()
  })

  it('opens on an existing link as an edit, prefilled', () => {
    renderDialog({ existing: { kind: 'external', href: 'https://old.example' }, initialText: 'old text' })

    expect(screen.getByRole('heading', { name: 'Edit link' })).toBeInTheDocument()
    expect(screen.getByLabelText('Web address')).toHaveValue('https://old.example')
    expect(screen.getByLabelText('Text')).toHaveValue('old text')
    expect(screen.getByRole('button', { name: 'Update' })).toBeEnabled()
  })

  it('offers Remove link only when there is a link to remove', () => {
    const onRemove = vi.fn()
    renderDialog({ existing: { kind: 'page', pageId: 'page-2' }, initialText: 'x', onRemove })
    fireEvent.click(screen.getByRole('button', { name: 'Remove link' }))
    expect(onRemove).toHaveBeenCalled()
  })

  it('offers no Remove on a fresh insert', () => {
    renderDialog({ initialText: 'x' })
    expect(screen.queryByRole('button', { name: 'Remove link' })).not.toBeInTheDocument()
  })

  it('has no axe violations open', async () => {
    renderDialog({ initialText: 'x' })
    await waitFor(() => expect(screen.getByLabelText('Web address')).toBeInTheDocument())
    await expectNoAxeViolations()
  })
})
