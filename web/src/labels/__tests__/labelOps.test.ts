import { describe, expect, it } from 'vitest'
import { computeLabelOps, type LabelRefLite } from '../labelOps'

const eng: LabelRefLite = { id: 'l-eng', name: 'engineering' }
const onboarding: LabelRefLite = { id: 'l-onb', name: 'onboarding' }
const spaceLabels: LabelRefLite[] = [eng, onboarding]

describe('computeLabelOps', () => {
  it('plans nothing when desired equals current', () => {
    expect(computeLabelOps([eng], ['engineering'], spaceLabels)).toEqual({ create: [], attach: [], detach: [] })
  })

  it('attaches an existing space label by id', () => {
    expect(computeLabelOps([], ['onboarding'], spaceLabels)).toEqual({ create: [], attach: [onboarding], detach: [] })
  })

  it('creates a label that does not exist in the space yet', () => {
    expect(computeLabelOps([], ['brand-new'], spaceLabels)).toEqual({ create: ['brand-new'], attach: [], detach: [] })
  })

  it('detaches a removed label by id', () => {
    expect(computeLabelOps([eng, onboarding], ['engineering'], spaceLabels)).toEqual({
      create: [],
      attach: [],
      detach: [onboarding],
    })
  })

  it('plans a mixed edit — create + attach + detach in one save', () => {
    const ops = computeLabelOps([eng], ['onboarding', 'brand-new'], spaceLabels)
    expect(ops.create).toEqual(['brand-new'])
    expect(ops.attach).toEqual([onboarding])
    expect(ops.detach).toEqual([eng])
  })

  it('ignores blank and duplicate names', () => {
    expect(computeLabelOps([], ['  ', 'onboarding', 'onboarding'], spaceLabels)).toEqual({
      create: [],
      attach: [onboarding],
      detach: [],
    })
  })

  it('matches names exactly (ordinal) — no case folding', () => {
    // Consistent with the system's exact-match rule: "Engineering" is a
    // different label from "engineering", so it's a create, not an attach.
    const ops = computeLabelOps([], ['Engineering'], spaceLabels)
    expect(ops.create).toEqual(['Engineering'])
    expect(ops.attach).toEqual([])
  })
})
