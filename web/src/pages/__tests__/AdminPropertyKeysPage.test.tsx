import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { AdminPropertyKeysPage } from '../AdminPropertyKeysPage'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * The instance-admin key registry (design.md §20.1). The refusal that matters
 * is deleting a key pages still use: it is refused rather than cascaded, and
 * the server's message carries the page count — the only disclosure §6.7
 * allows, and the part an admin needs.
 */

const baseKeys = [
  { id: 'k-owner', key: 'Owner', description: 'Who to ask about this page.', sortOrder: 0 },
  { id: 'k-status', key: 'Status', description: null, sortOrder: 1 },
]

interface Options {
  keys?: typeof baseKeys
  createError?: Record<string, unknown> | null
  deleteError?: Record<string, unknown> | null
}

function renderPage(options: Options = {}) {
  const { keys = baseKeys, createError = null, deleteError = null } = options
  const mock = createMockUrqlClient((name) => {
    if (name === 'PagePropertyKeys') return { pagePropertyKeys: keys }
    if (name === 'CreatePagePropertyKey')
      return {
        createPagePropertyKey: {
          propertyKey: createError
            ? null
            : { id: 'k-review', key: 'Review Date', description: null, sortOrder: 2 },
          error: createError,
        },
      }
    if (name === 'DeletePagePropertyKey')
      return { deletePagePropertyKey: { deletedPropertyKeyId: deleteError ? null : 'k-owner', error: deleteError } }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <AdminPropertyKeysPage />
    </UrqlProvider>,
  )
  return mock
}

const mutations = (mock: ReturnType<typeof renderPage>, name: string) =>
  mock.operations.filter((op) => op.name === name).map((op) => op.variables)

describe('AdminPropertyKeysPage accessibility', () => {
  it('has no axe violations, including with the delete confirmation open', async () => {
    renderPage()
    await screen.findByText('Owner')
    await expectNoAxeViolations()
    fireEvent.click(screen.getByRole('button', { name: 'Delete Owner' }))
    await screen.findByText('Delete Owner?')
    await expectNoAxeViolations()
  })
})

describe('AdminPropertyKeysPage registry list', () => {
  it('lists keys with their descriptions and a per-row delete affordance', async () => {
    renderPage()
    expect(await screen.findByText('Owner')).toBeInTheDocument()
    expect(screen.getByText('Who to ask about this page.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete Status' })).toBeInTheDocument()
  })

  it('states the consequence of an empty registry, not just the fact', async () => {
    renderPage({ keys: [] })
    expect(
      await screen.findByText('No property keys yet — until one exists, page editors have no properties they can set.'),
    ).toBeInTheDocument()
  })
})

describe('AdminPropertyKeysPage creating a key', () => {
  it('sends the trimmed key and a null description when none was typed', async () => {
    const mock = renderPage()
    await screen.findByText('Owner')
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: '  Review Date  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add key' }))

    await waitFor(() => expect(mutations(mock, 'CreatePagePropertyKey')).toHaveLength(1))
    expect(mutations(mock, 'CreatePagePropertyKey')[0]).toEqual({
      input: { key: 'Review Date', description: null },
    })
  })

  it('carries a description through when one is given', async () => {
    const mock = renderPage()
    await screen.findByText('Owner')
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'Review Date' } })
    fireEvent.change(screen.getByLabelText('Description (optional)'), { target: { value: 'Next review.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add key' }))

    await waitFor(() => expect(mutations(mock, 'CreatePagePropertyKey')).toHaveLength(1))
    expect(mutations(mock, 'CreatePagePropertyKey')[0]).toEqual({
      input: { key: 'Review Date', description: 'Next review.' },
    })
  })

  it('never offers an empty key', async () => {
    renderPage()
    await screen.findByText('Owner')
    expect(screen.getByRole('button', { name: 'Add key' })).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: '   ' } })
    expect(screen.getByRole('button', { name: 'Add key' })).toBeDisabled()
  })

  it("shows the server's duplicate refusal rather than guessing at the rule", async () => {
    renderPage({
      createError: { kind: 'Validation', message: "A property key named 'Owner' already exists." },
    })
    await screen.findByText('Owner')
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'owner' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add key' }))
    expect(await screen.findByText("A property key named 'Owner' already exists.")).toBeInTheDocument()
  })
})

describe('AdminPropertyKeysPage deleting a key', () => {
  it('confirms first, then deletes by id', async () => {
    const mock = renderPage()
    await screen.findByText('Owner')
    fireEvent.click(screen.getByRole('button', { name: 'Delete Owner' }))
    expect(await screen.findByText('Delete Owner?')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))

    await waitFor(() => expect(mutations(mock, 'DeletePagePropertyKey')).toHaveLength(1))
    expect(mutations(mock, 'DeletePagePropertyKey')[0]).toEqual({ keyId: 'k-owner' })
  })

  it('cancel closes the dialog without deleting anything', async () => {
    const mock = renderPage()
    await screen.findByText('Owner')
    fireEvent.click(screen.getByRole('button', { name: 'Delete Owner' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByText('Delete Owner?')).not.toBeInTheDocument())
    expect(mutations(mock, 'DeletePagePropertyKey')).toHaveLength(0)
  })

  it('shows the in-use refusal verbatim, because the count is the useful part', async () => {
    renderPage({
      deleteError: {
        kind: 'Validation',
        message: "Property key 'Owner' is in use on 12 page(s). Remove those values before deleting the key.",
      },
    })
    await screen.findByText('Owner')
    fireEvent.click(screen.getByRole('button', { name: 'Delete Owner' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }))
    expect(
      await screen.findByText(
        "Property key 'Owner' is in use on 12 page(s). Remove those values before deleting the key.",
      ),
    ).toBeInTheDocument()
  })
})
