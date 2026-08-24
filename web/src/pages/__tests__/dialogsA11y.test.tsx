import { describe, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { DeletePageDialog } from '../DeletePageDialog'
import { ReadOnlyReplicaDialog } from '../ReadOnlyReplicaDialog'
import { RenameSpaceDialog } from '../RenameSpaceDialog'
import { expectNoAxeViolations } from '../../test/axe'

/**
 * Axe passes for the presentation-only dialogs that have no behavior test
 * file of their own (docs/ACCESSIBILITY.md — every dialog gets a jsdom axe
 * pass in its OPEN state, since MUI dialogs portal into document.body).
 * Behavior for these dialogs is covered where they're used (PageViewPage,
 * PageEditPage, SpaceBrowserPage tests).
 */
describe('dialogs — axe passes (WCAG 2.2 AA policy, test/axe.ts)', () => {
  it('DeletePageDialog open, cascade warning + blocked-descendants alert', async () => {
    render(
      <DeletePageDialog
        open
        onClose={() => {}}
        pageTitle="Static fire campaign"
        hasChildren
        onConfirm={() => {}}
        blockedDescendantCount={2}
      />,
    )
    screen.getByRole('dialog')
    await expectNoAxeViolations()
  })

  it('ReadOnlyReplicaDialog open', async () => {
    render(<ReadOnlyReplicaDialog open originInstanceId="LOW" onClose={() => {}} />)
    screen.getByRole('dialog')
    await expectNoAxeViolations()
  })

  it('RenameSpaceDialog open', async () => {
    render(
      <RenameSpaceDialog
        open
        spaceName="Propulsion"
        value="Propulsion"
        onValueChange={() => {}}
        onCancel={() => {}}
        onConfirm={() => {}}
      />,
    )
    screen.getByRole('dialog')
    await expectNoAxeViolations()
  })
})
