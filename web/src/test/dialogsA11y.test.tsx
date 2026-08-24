import { describe, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { DeletePageDialog } from '../trash/DeletePageDialog'
import { ReadOnlyReplicaDialog } from '../feedback/ReadOnlyReplicaDialog'
import { RenameSpaceDialog } from '../spaces/RenameSpaceDialog'
import { expectNoAxeViolations } from './axe'

/**
 * Axe passes for the presentation-only dialogs that have no behavior test
 * file of their own (docs/ACCESSIBILITY.md — every dialog gets a jsdom axe
 * pass in its OPEN state, since MUI dialogs portal into document.body).
 * Behavior for these dialogs is covered where they're used (PageViewPage,
 * PageEditPage, SpaceBrowserPage tests).
 *
 * Lives beside the axe policy it applies rather than in any one feature's
 * __tests__: its subjects now sit in three different folders, and the point
 * of the suite is the RULE (every dialog, open, axe-clean) — a newcomer
 * adding a dialog anywhere in the app should find one obvious place to add
 * a line, not have to notice the convention three folders away.
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
