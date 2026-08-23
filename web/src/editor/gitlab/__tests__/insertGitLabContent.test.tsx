import { createRef } from 'react'
import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { RichTextEditor, type RichTextEditorHandle } from '../../RichTextEditor'
import { createMockUrqlClient } from '../../../test/mockUrqlClient'

/**
 * The editor's GitLab insert affordances (design.md §18): a toolbar menu —
 * present only when the instance has GitLab configured (fail-closed
 * affordances) — whose dialogs write the scheme form / canonical fence
 * bodies. Asserted end-to-end against the serialized Markdown, because the
 * stored bytes are the contract.
 */

function renderEditor({ configured = true } = {}) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured, baseUrl: configured ? 'https://gitlab.example.com' : null, viewerHasToken: true } }
    return undefined
  })
  const ref = createRef<RichTextEditorHandle>()
  render(
    <UrqlProvider value={mock.client}>
      <RichTextEditor ref={ref} initialMarkdown="" editable showToolbar />
    </UrqlProvider>,
  )
  return { mock, ref }
}

function openGitlabMenu() {
  fireEvent.click(screen.getByRole('button', { name: 'Insert GitLab content' }))
}

describe('GitLab toolbar menu gating', () => {
  it('is hidden entirely when the instance has no GitLab configured', async () => {
    renderEditor({ configured: false })
    expect(await screen.findByRole('toolbar', { name: 'Formatting' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Insert GitLab content' })).not.toBeInTheDocument()
  })

  it('is present when configured', async () => {
    renderEditor()
    expect(await screen.findByRole('button', { name: 'Insert GitLab content' })).toBeInTheDocument()
  })
})

describe('insert issue link dialog', () => {
  it('manual project + iid insert the scheme-form link with the given text', async () => {
    const { ref } = renderEditor()
    await screen.findByRole('button', { name: 'Insert GitLab content' })
    openGitlabMenu()
    fireEvent.click(screen.getByRole('menuitem', { name: 'Issue link' }))

    fireEvent.change(screen.getByLabelText(/^Project/), { target: { value: 'propulsion/turbopump' } })
    fireEvent.change(screen.getByLabelText(/^Issue number/), { target: { value: '57' } })
    fireEvent.change(screen.getByLabelText('Link text'), { target: { value: 'the pump issue' } })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    expect(ref.current?.getMarkdown()).toBe('[the pump issue](gitlab-issue://propulsion/turbopump/57)\n')
  })

  it('pasting a GitLab URL converts to the scheme form — the host is stripped (§18 explicit affordance)', async () => {
    const { ref } = renderEditor()
    await screen.findByRole('button', { name: 'Insert GitLab content' })
    openGitlabMenu()
    fireEvent.click(screen.getByRole('menuitem', { name: 'Issue link' }))

    fireEvent.change(screen.getByLabelText(/Paste a GitLab issue URL/), {
      target: { value: 'https://secret-host.example.com/group/proj/-/issues/42' },
    })
    // Project and iid were filled from the URL.
    expect(screen.getByLabelText(/^Project/)).toHaveValue('group/proj')
    expect(screen.getByLabelText(/^Issue number/)).toHaveValue('42')

    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))
    const markdown = ref.current?.getMarkdown() ?? ''
    // Default text is project#iid; the stored bytes carry no hostname.
    expect(markdown).toBe('[group/proj#42](gitlab-issue://group/proj/42)\n')
    expect(markdown).not.toContain('secret-host')
  })

  it('refuses to insert while the reference is incomplete', async () => {
    renderEditor()
    await screen.findByRole('button', { name: 'Insert GitLab content' })
    openGitlabMenu()
    fireEvent.click(screen.getByRole('menuitem', { name: 'Issue link' }))
    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
    fireEvent.change(screen.getByLabelText(/^Issue number/), { target: { value: 'abc' } })
    expect(screen.getByRole('button', { name: 'Insert' })).toBeDisabled()
  })
})

describe('insert file embed dialog', () => {
  it('writes the canonical gitlab-file fence body', async () => {
    const { ref } = renderEditor()
    await screen.findByRole('button', { name: 'Insert GitLab content' })
    openGitlabMenu()
    fireEvent.click(screen.getByRole('menuitem', { name: 'File embed' }))

    fireEvent.change(screen.getByLabelText(/^Project/), { target: { value: 'propulsion/turbopump' } })
    fireEvent.change(screen.getByLabelText(/^File path/), { target: { value: 'docs/spec.md' } })
    fireEvent.change(screen.getByLabelText(/^Ref/), { target: { value: 'main' } })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    expect(ref.current?.getMarkdown()).toContain(
      '```gitlab-file\nproject=propulsion/turbopump\npath=docs/spec.md\nref=main\n```',
    )
  })

  it('omits ref when left blank (server defaults to HEAD)', async () => {
    const { ref } = renderEditor()
    await screen.findByRole('button', { name: 'Insert GitLab content' })
    openGitlabMenu()
    fireEvent.click(screen.getByRole('menuitem', { name: 'File embed' }))

    fireEvent.change(screen.getByLabelText(/^Project/), { target: { value: '142' } })
    fireEvent.change(screen.getByLabelText(/^File path/), { target: { value: 'README.md' } })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    expect(ref.current?.getMarkdown()).toContain('```gitlab-file\nproject=142\npath=README.md\n```')
  })
})

describe('insert issue list dialog', () => {
  it('builds the filter body from the fields, omitting the empty ones', async () => {
    const { ref } = renderEditor()
    await screen.findByRole('button', { name: 'Insert GitLab content' })
    openGitlabMenu()
    fireEvent.click(screen.getByRole('menuitem', { name: 'Issue list' }))

    fireEvent.change(screen.getByLabelText(/^Project/), { target: { value: 'propulsion/turbopump' } })
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'State' }))
    fireEvent.click(screen.getByRole('option', { name: 'Open' }))
    fireEvent.change(screen.getByLabelText('Labels'), { target: { value: 'bug, ops' } })
    fireEvent.change(screen.getByLabelText('Max results'), { target: { value: '10' } })
    fireEvent.click(screen.getByRole('button', { name: 'Insert' }))

    expect(ref.current?.getMarkdown()).toContain(
      '```gitlab-issues\nproject=propulsion/turbopump\nstate=opened\nlabels=bug,ops\nfirst=10\n```',
    )
  })
})
