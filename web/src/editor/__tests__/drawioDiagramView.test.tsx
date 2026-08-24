import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { RichTextEditor } from '../RichTextEditor'

const PAYLOAD = 'PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIGNvbnRlbnQ9IiZsdDtteGZpbGUmZ3Q7Jmx0Oy9teGZpbGUmZ3Q7Ij48cmVjdCB3aWR0aD0iMTAiIGhlaWdodD0iMTAiLz48L3N2Zz4='
const DRAWIO_MD = `\`\`\`drawio\n${PAYLOAD}\n\`\`\`\n`

describe('drawio diagram node — page view (read mode)', () => {
  it('renders the payload as an inert data-URI <img> (no iframe/object, no viewer JS)', () => {
    const { container } = render(
      <RichTextEditor initialMarkdown={DRAWIO_MD} editable={false} showToolbar={false} />,
    )
    const img = screen.getByRole('img', { name: 'draw.io diagram' })
    expect(img.tagName).toBe('IMG')
    expect(img).toHaveAttribute('src', `data:image/svg+xml;base64,${PAYLOAD}`)
    expect(container.querySelector('iframe')).toBeNull()
    expect(container.querySelector('object')).toBeNull()
  })

  it('an author-supplied alt (the fence body alt: line) becomes the accessible name; the generic string is only the fallback', () => {
    render(
      <RichTextEditor
        initialMarkdown={`\`\`\`drawio\nalt: Feed system overview\n${PAYLOAD}\n\`\`\`\n`}
        editable={false}
        showToolbar={false}
      />,
    )
    expect(screen.getByRole('img', { name: 'Feed system overview' })).toBeInTheDocument()
    expect(screen.queryByRole('img', { name: 'draw.io diagram' })).not.toBeInTheDocument()
  })

  it('read mode offers no edit affordance', () => {
    render(<RichTextEditor initialMarkdown={DRAWIO_MD} editable={false} showToolbar={false} />)
    expect(screen.queryByRole('button', { name: 'Edit diagram' })).not.toBeInTheDocument()
  })

  it('a garbled payload renders an inline error, not a crash and not an <img>', () => {
    render(
      <RichTextEditor initialMarkdown={'```drawio\nnot really base64!!\n```\n'} editable={false} showToolbar={false} />,
    )
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent("Diagram can't be displayed.")
    expect(screen.queryByRole('img', { name: 'draw.io diagram' })).not.toBeInTheDocument()
  })
})

describe('drawio diagram node — editing', () => {
  it('shows the Edit diagram affordance', () => {
    render(<RichTextEditor initialMarkdown={DRAWIO_MD} editable showToolbar={false} />)
    expect(screen.getByRole('button', { name: 'Edit diagram' })).toBeInTheDocument()
  })

  it('an empty diagram (freshly inserted fence) shows a hint plus the edit button', () => {
    render(<RichTextEditor initialMarkdown={'```drawio\n\n```\n'} editable showToolbar={false} />)
    expect(screen.getByText(/Empty draw\.io diagram/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit diagram' })).toBeInTheDocument()
  })

  it('with no VITE_DRAWIO_URL configured, Edit opens the fail-closed explainer instead of any external editor', async () => {
    // Vitest does not define VITE_DRAWIO_URL, so this exercises the real
    // default path: readDrawioConfig(import.meta.env) === null.
    render(<RichTextEditor initialMarkdown={DRAWIO_MD} editable showToolbar={false} />)

    fireEvent.click(screen.getByRole('button', { name: 'Edit diagram' }))
    expect(await screen.findByText('No diagram editor configured')).toBeInTheDocument()
    expect(document.querySelector('iframe')).toBeNull()
  })
})
