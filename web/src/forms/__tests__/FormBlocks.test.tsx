import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { Provider as UrqlProvider } from 'urql'
import { FormDefinitionBlock, FormListBlock } from '../FormBlocks'
import { PageIdContext } from '../../pages/pageContext'
import { createMockUrqlClient } from '../../test/mockUrqlClient'
import { expectNoAxeViolations } from '../../test/axe'

const PAGE_ID = 'page-1'

const definition = {
  collection: 'incident-report',
  fields: [
    { name: 'severity', type: 'SELECT', required: true, options: ['low', 'medium', 'high'] },
    { name: 'summary', type: 'TEXT', required: true, options: [] },
    { name: 'occurredAt', type: 'DATE', required: false, options: [] },
    { name: 'costEstimate', type: 'NUMBER', required: false, options: [] },
  ],
}

const official = { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], prefix: 'UK', label: 'UK OFFICIAL' }
const secret = { level: 'SECRET', levelName: 'SECRET', eyesOnly: [], prefix: 'UK', label: 'UK SECRET' }

const entries = [
  {
    id: 'e1',
    collection: 'incident-report',
    data: JSON.stringify({ severity: 'high', summary: 'Turbopump stall', occurredAt: '2026-08-14' }),
    version: 1,
    createdAtUtc: '2026-08-14T00:00:00Z',
    marking: official,
  },
  {
    id: 'e2',
    collection: 'incident-report',
    data: JSON.stringify({ severity: 'low', summary: 'Sensor drift' }),
    version: 1,
    createdAtUtc: '2026-08-15T00:00:00Z',
    marking: secret,
  },
]

function renderBlock(
  ui: React.ReactElement,
  {
    definitions = [definition] as unknown[],
    errors = [] as unknown[],
    rows = entries as unknown[],
    createError = null as unknown,
    pageId = PAGE_ID as string | null,
  } = {},
) {
  const mock = createMockUrqlClient((name) => {
    if (name === 'PageForms') return { pageForms: { definitions, errors } }
    if (name === 'PageEntries') return { pageEntries: rows }
    if (name === 'CreatePageEntry')
      return { createPageEntry: { entry: createError ? null : entries[0], error: createError } }
    return undefined
  })
  render(
    <UrqlProvider value={mock.client}>
      <PageIdContext value={pageId}>{ui}</PageIdContext>
    </UrqlProvider>,
  )
  return mock
}

describe('FormDefinitionBlock', () => {
  it('renders a control per declared field, of the declared type', async () => {
    renderBlock(<FormDefinitionBlock collection="incident-report" />)
    expect(await screen.findByLabelText(/severity/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/summary/i)).toHaveAttribute('type', 'text')
    expect(screen.getByLabelText(/occurredAt/i)).toHaveAttribute('type', 'date')
    expect(screen.getByLabelText(/costEstimate/i)).toHaveAttribute('type', 'number')
  })

  it('offers a select its declared options, in the author\'s order', async () => {
    // The order is the picker's order, so the parser keeps it as written rather
    // than sorting — this pins that it survives to the control.
    renderBlock(<FormDefinitionBlock collection="incident-report" />)
    fireEvent.mouseDown(await screen.findByLabelText(/severity/i))
    const options = screen.getAllByRole('option').map((o) => o.textContent)
    expect(options).toEqual(['low', 'medium', 'high'])
  })

  it('will not submit until every required field is filled', async () => {
    renderBlock(<FormDefinitionBlock collection="incident-report" />)
    const submit = await screen.findByRole('button', { name: 'Submit' })
    expect(submit).toBeDisabled()

    fireEvent.mouseDown(screen.getByLabelText(/severity/i))
    fireEvent.click(screen.getByRole('option', { name: 'high' }))
    expect(submit).toBeDisabled() // summary still empty

    fireEvent.change(screen.getByLabelText(/summary/i), { target: { value: 'Turbopump stall' } })
    expect(submit).toBeEnabled()
  })

  it('sends the filled values as the entry, against this page and collection', async () => {
    const mock = renderBlock(<FormDefinitionBlock collection="incident-report" />)
    fireEvent.mouseDown(await screen.findByLabelText(/severity/i))
    fireEvent.click(screen.getByRole('option', { name: 'high' }))
    fireEvent.change(screen.getByLabelText(/summary/i), { target: { value: 'Turbopump stall' } })
    fireEvent.click(screen.getByRole('button', { name: 'Submit' }))

    await waitFor(() => {
      const call = mock.operations.find((op) => op.name === 'CreatePageEntry')
      expect(call?.variables['input']).toEqual({
        pageId: PAGE_ID,
        collection: 'incident-report',
        data: JSON.stringify({ severity: 'high', summary: 'Turbopump stall' }),
      })
    })
  })

  it('clears itself after a save, so the next submission is a new record', async () => {
    // A form that kept the last submission invites accidental duplicates.
    renderBlock(<FormDefinitionBlock collection="incident-report" />)
    fireEvent.mouseDown(await screen.findByLabelText(/severity/i))
    fireEvent.click(screen.getByRole('option', { name: 'high' }))
    fireEvent.change(screen.getByLabelText(/summary/i), { target: { value: 'Turbopump stall' } })
    fireEvent.click(screen.getByRole('button', { name: 'Submit' }))

    expect(await screen.findByText('Saved.')).toBeInTheDocument()
    expect(screen.getByLabelText(/summary/i)).toHaveValue('')
  })

  it('surfaces a refusal from the server instead of claiming success', async () => {
    renderBlock(<FormDefinitionBlock collection="incident-report" />, {
      createError: {
        kind: 'Forbidden',
        message: 'classification:secret',
        expectedRevisionNumber: null,
        actualRevisionNumber: null,
        latestTitle: null,
        latestContent: null,
        spaceId: null,
        originInstanceId: null,
        blockedPageCount: null,
        notFoundId: null,
      },
    })
    fireEvent.mouseDown(await screen.findByLabelText(/severity/i))
    fireEvent.click(screen.getByRole('option', { name: 'high' }))
    fireEvent.change(screen.getByLabelText(/summary/i), { target: { value: 'x' } })
    fireEvent.click(screen.getByRole('button', { name: 'Submit' }))

    expect(await screen.findByText(/Not permitted/)).toBeInTheDocument()
    expect(screen.queryByText('Saved.')).toBeNull()
  })

  it("shows the server's parse error verbatim rather than losing the form", async () => {
    // A malformed definition presents to its author as "my form disappeared", so
    // the message naming the collection and the line is the whole point.
    renderBlock(<FormDefinitionBlock collection="incident-report" />, {
      definitions: [],
      errors: [{ collection: 'incident-report', message: "The select field 'severity' lists no options." }],
    })
    expect(await screen.findByText(/lists no options/)).toBeInTheDocument()
  })

  it('says so when the page declares no such form', async () => {
    renderBlock(<FormDefinitionBlock collection="incident-report" />, { definitions: [], errors: [] })
    expect(await screen.findByText(/No form named "incident-report"/)).toBeInTheDocument()
  })

  it('says so outside a saved page rather than querying with no id', async () => {
    renderBlock(<FormDefinitionBlock collection="incident-report" />, { pageId: null })
    expect(await screen.findByText(/only works on a saved page/)).toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    renderBlock(<FormDefinitionBlock collection="incident-report" />)
    await screen.findByLabelText(/severity/i)
    await expectNoAxeViolations()
  })
})

describe('FormListBlock', () => {
  it('takes its columns from the definition, not from the records', async () => {
    // A field nobody has filled in still gets a column, so the table does not
    // change shape as records arrive.
    renderBlock(<FormListBlock collection="incident-report" columns={[]} />)
    const table = await screen.findByRole('table', { name: /incident-report records/ })
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent)
    expect(headers).toEqual(['severity', 'summary', 'occurredAt', 'costEstimate', 'Marking'])
  })

  it('narrows to the named columns when the fence names some', async () => {
    renderBlock(<FormListBlock collection="incident-report" columns={['summary', 'severity']} />)
    const table = await screen.findByRole('table', { name: /incident-report records/ })
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent)
    // The definition's order, not the fence's: the columns key selects, it does
    // not reorder, so one table cannot disagree with another about field order.
    expect(headers).toEqual(['severity', 'summary', 'Marking'])
  })

  it('badges every record with its own marking', async () => {
    // Entries on one page legitimately carry different markings, so a single
    // label over the table would be wrong about every row but one.
    renderBlock(<FormListBlock collection="incident-report" columns={[]} />)
    const table = await screen.findByRole('table', { name: /incident-report records/ })
    // textContent rather than getByText: the badge puts "Classification:" in a
    // visually-hidden span beside the level, so the phrase spans two elements —
    // which is the point of it, and is how the tree tests read it too.
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows[0]!.textContent).toContain('Classification: OFFICIAL')
    expect(rows[1]!.textContent).toContain('Classification: SECRET')
  })

  it('shows a record whose JSON will not parse as a blank row, not a broken table', async () => {
    renderBlock(<FormListBlock collection="incident-report" columns={['summary']} />, {
      rows: [{ ...entries[0], data: 'not json at all' }],
    })
    const table = await screen.findByRole('table', { name: /incident-report records/ })
    expect(within(table).getAllByRole('row')).toHaveLength(2) // header + the row
    expect(within(table).queryByText('Turbopump stall')).toBeNull()
  })

  it('distinguishes "no records yet" from "no such collection"', async () => {
    // Different facts, and a reader acts on them differently: one means fill the
    // form in, the other means the fence names something that is not defined.
    renderBlock(<FormListBlock collection="incident-report" columns={[]} />, { rows: [] })
    expect(await screen.findByText('No records yet.')).toBeInTheDocument()
  })

  it('says so when the page declares no such form', async () => {
    renderBlock(<FormListBlock collection="incident-report" columns={[]} />, { definitions: [], errors: [] })
    expect(await screen.findByText(/No form named "incident-report"/)).toBeInTheDocument()
  })

  it('reports no count of what it was not allowed to see', async () => {
    // §6.7: a pruned entry is indistinguishable from an absent one, so the block
    // renders what it was given and says nothing whatever about the rest. A
    // "showing 2 of 5" would be a census of the classified estate.
    renderBlock(<FormListBlock collection="incident-report" columns={[]} />)
    await screen.findByRole('table', { name: /incident-report records/ })
    expect(screen.queryByText(/showing/i)).toBeNull()
    expect(screen.queryByText(/hidden/i)).toBeNull()
    expect(screen.queryByText(/of \d+/)).toBeNull()
  })

  it('applies a where filter to the rows it was given', async () => {
    renderBlock(<FormListBlock collection="incident-report" columns={['summary']} where="severity = high" />)
    const table = await screen.findByRole('table', { name: /incident-report records/ })
    expect(within(table).getByText('Turbopump stall')).toBeInTheDocument()
    expect(within(table).queryByText('Sensor drift')).toBeNull()
  })

  it('refuses a filter it cannot read rather than ignoring it', async () => {
    // The bug this replaces: `where` was dropped entirely, so a filter written
    // from the documented example silently returned everything and the author
    // read the rows as though it had applied.
    renderBlock(<FormListBlock collection="incident-report" columns={[]} where="severity = high OR severity = low" />)
    expect(await screen.findByText(/OR is not supported/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).toBeNull()
  })

  it('names a field the form does not declare', async () => {
    // A typo would otherwise match nothing and present as "no records yet".
    renderBlock(<FormListBlock collection="incident-report" columns={[]} where="sevrity = high" />)
    expect(await screen.findByText(/"sevrity" is not a field of incident-report/)).toBeInTheDocument()
  })

  it('has no axe violations with rows present', async () => {
    renderBlock(<FormListBlock collection="incident-report" columns={[]} />)
    await screen.findByRole('table', { name: /incident-report records/ })
    await expectNoAxeViolations()
  })
})
