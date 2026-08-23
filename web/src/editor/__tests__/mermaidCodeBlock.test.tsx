import { describe, expect, it, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { RichTextEditor } from '../RichTextEditor'
import { renderMermaid } from '../diagrams/mermaidRenderer'

/**
 * Mermaid rendering is mocked AT THE SEAM (diagrams/mermaidRenderer.ts):
 * mermaid's real renderer needs SVG text-measurement APIs jsdom does not
 * implement, so what these tests prove is the NodeView contract — which
 * blocks call the renderer, what happens with the result, and that a
 * failed render is an inline error instead of a crash. Real mermaid output
 * is exercised only in a browser.
 */
vi.mock('../diagrams/mermaidRenderer', () => ({
  renderMermaid: vi.fn(),
}))

const renderMermaidMock = vi.mocked(renderMermaid)

beforeEach(() => {
  renderMermaidMock.mockReset()
})

const MERMAID_MD = '```mermaid\ngraph TD\n  A --> B\n```\n'

describe('mermaid code blocks — page view (read mode)', () => {
  it('renders the diagram SVG from the fence source', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'ok', svg: '<svg data-marker="mermaid-out"></svg>' })
    render(<RichTextEditor initialMarkdown={MERMAID_MD} editable={false} showToolbar={false} />)

    const figure = await screen.findByRole('img', { name: 'Mermaid diagram' })
    expect(figure.innerHTML).toContain('data-marker="mermaid-out"')
    expect(renderMermaidMock).toHaveBeenCalledExactlyOnceWith('graph TD\n  A --> B')
  })

  it('read mode marks the block readonly so CSS shows the diagram alone', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'ok', svg: '<svg></svg>' })
    const { container } = render(
      <RichTextEditor initialMarkdown={MERMAID_MD} editable={false} showToolbar={false} />,
    )
    await screen.findByRole('img', { name: 'Mermaid diagram' })
    const block = container.querySelector('.rw-mermaid-block')
    expect(block).toHaveClass('rw-mermaid-block-readonly')
    expect(block).toHaveAttribute('data-render-state', 'ok')
  })

  it('invalid diagram source renders an inline error block (with the source shown), never a crash', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'error', message: 'Parse error on line 1' })
    const { container } = render(
      <RichTextEditor initialMarkdown={'```mermaid\nnot a diagram\n```\n'} editable={false} showToolbar={false} />,
    )

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("Diagram doesn't render.")
    expect(alert).toHaveTextContent('Parse error on line 1')
    // The error state flips the wrapper so read-mode CSS un-hides the source.
    expect(container.querySelector('.rw-mermaid-block')).toHaveAttribute('data-render-state', 'error')
    expect(container.querySelector('.rw-mermaid-source')?.textContent).toContain('not a diagram')
  })

  it('non-mermaid code blocks never touch the mermaid renderer', () => {
    render(<RichTextEditor initialMarkdown={'```js\nconst a = 1;\n```\n'} editable={false} showToolbar={false} />)
    expect(screen.queryByRole('img', { name: 'Mermaid diagram' })).not.toBeInTheDocument()
    expect(renderMermaidMock).not.toHaveBeenCalled()
    expect(screen.getByText('const a = 1;')).toBeInTheDocument()
  })
})

describe('mermaid code blocks — editing', () => {
  it('shows source and live preview side by side', async () => {
    renderMermaidMock.mockResolvedValue({ status: 'ok', svg: '<svg></svg>' })
    const { container } = render(<RichTextEditor initialMarkdown={MERMAID_MD} editable showToolbar={false} />)

    await screen.findByRole('img', { name: 'Mermaid diagram' })
    const block = container.querySelector('.rw-mermaid-block')
    expect(block).toHaveClass('rw-mermaid-block-editing')
    // Source stays visible (and editable) alongside the preview.
    expect(container.querySelector('.rw-mermaid-source')?.textContent).toContain('graph TD')
  })

  it('an empty mermaid block shows a hint instead of calling the renderer', async () => {
    render(<RichTextEditor initialMarkdown={'```mermaid\n\n```\n'} editable showToolbar={false} />)
    expect(await screen.findByText('Type Mermaid source to see a diagram preview.')).toBeInTheDocument()
    expect(renderMermaidMock).not.toHaveBeenCalled()
  })
})
