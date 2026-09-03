import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { RichTextEditor } from '../RichTextEditor'
import type { PageLinkTargetLike } from '../marks/pageLinkTargets'
import { expectNoAxeViolations } from '../../test/axe'

const MARKDOWN = 'See [the procedure](page://id-a), [the test data](page://id-b) and [an old note](page://id-c).\n'

const targets: PageLinkTargetLike[] = [
  { id: 'id-a', page: { id: 'id-a', title: 'Chill-in procedure', slug: 'chill-in', spaceKey: 'PROP' }, denial: null },
  { id: 'id-b', page: null, denial: { placeholderTitle: '(protected)', marking: { label: 'UK TOP SECRET UK EYES ONLY' } } },
  { id: 'id-c', page: null, denial: null },
]

function renderView(props: { linkTargets?: PageLinkTargetLike[]; editable?: boolean } = {}) {
  return render(
    <MemoryRouter>
      <RichTextEditor initialMarkdown={MARKDOWN} editable={props.editable ?? false} showToolbar={false} linkTargets={props.linkTargets} />
    </MemoryRouter>,
  )
}

/**
 * The pageLink mark's read-mode rendering (design.md §4, §6.7 / §21.8).
 * The targets ride on the page read; nothing here fetches. Through the real
 * `RichTextEditor` so the mark view runs inside the same ProseMirror the
 * page view mounts — the one-renderer rule cuts both ways.
 */
describe('PageLinkView', () => {
  it('renders a readable target as a router link to its readable address, keeping the author text', async () => {
    renderView({ linkTargets: targets })
    const link = await screen.findByRole('link', { name: 'the procedure' })
    expect(link).toHaveAttribute('href', '/spaces/PROP/chill-in')
  })

  it('renders a withheld target inert, with the marker after the author text and the label only on hover', async () => {
    renderView({ linkTargets: targets })
    await screen.findByRole('link', { name: 'the procedure' })
    expect(screen.queryByRole('link', { name: /the test data/ })).toBeNull()
    const inert = screen.getByText('the test data').closest('.rw-page-link-protected')
    expect(inert).not.toBeNull()
    expect(inert).toHaveAttribute('title', 'Protected page, marked UK TOP SECRET UK EYES ONLY.')
    // The visible marker is the server's placeholder title, and the label is
    // NOT printed inline — a page of prose is not where every linked page's
    // classification gets listed.
    expect(inert?.querySelector('.rw-page-link-marker')?.textContent).toBe('(protected)')
    const visibleText = [...(inert?.childNodes ?? [])]
      .filter((node) => !(node instanceof HTMLElement && node.classList.contains('rw-visually-hidden')))
      .map((node) => node.textContent)
      .join('')
    expect(visibleText).not.toContain('TOP SECRET')
  })

  it('renders a vanished target inert with the missing marker', async () => {
    renderView({ linkTargets: targets })
    await screen.findByRole('link', { name: 'the procedure' })
    expect(screen.queryByRole('link', { name: /an old note/ })).toBeNull()
    const inert = screen.getByText('an old note').closest('.rw-page-link-missing')
    expect(inert).toHaveAttribute('title', 'This page no longer exists.')
  })

  it('falls back to the id address when nothing resolved the links (a comment, a help topic)', async () => {
    renderView()
    expect(await screen.findByRole('link', { name: 'the procedure' })).toHaveAttribute('href', '/pages/id-a')
    expect(screen.getByRole('link', { name: 'the test data' })).toHaveAttribute('href', '/pages/id-b')
  })

  it('renders plain editable spans in edit mode — no links, no markers, nothing fetched', async () => {
    // No urql provider is mounted at all: the view has no query to run in
    // any mode, and this proves edit mode does not even need one.
    renderView({ linkTargets: targets, editable: true })
    await screen.findByRole('textbox')
    expect(screen.queryByRole('link')).toBeNull()
    expect(document.querySelector('.rw-page-link-marker')).toBeNull()
    expect(screen.getByText('the test data').closest('.rw-page-link')).not.toBeNull()
  })

  it('has no axe violations with all three states rendered', async () => {
    renderView({ linkTargets: targets })
    await screen.findByRole('link', { name: 'the procedure' })
    await expectNoAxeViolations()
  })
})
