/** The id+name subset of `LabelRef` the attach/detach planner needs. */
export interface LabelRefLite {
  id: string
  name: string
}

export interface LabelOps {
  /** Names with no existing label in the space — `createLabel` first, then attach the returned id. */
  create: string[]
  /** Existing space labels to attach to the page. */
  attach: LabelRefLite[]
  /** Currently-attached labels to detach from the page. */
  detach: LabelRefLite[]
}

/**
 * Plans the mutations that turn a page's current label set into the
 * desired one. Names are the editor's currency (design.md §5 sidebar UX),
 * but the API's attach/detach/create take label ids — this maps between
 * them. Matching is exact (ordinal), consistent with the rest of the
 * system: "Onboarding" and "onboarding" are two different labels, and
 * guessing at what the author meant is not this function's job.
 */
export function computeLabelOps(
  current: LabelRefLite[],
  desired: string[],
  spaceLabels: LabelRefLite[],
): LabelOps {
  const desiredNames = [...new Set(desired.map((name) => name.trim()).filter((name) => name.length > 0))]
  const currentByName = new Map(current.map((label) => [label.name, label]))
  const spaceByName = new Map(spaceLabels.map((label) => [label.name, label]))

  const create: string[] = []
  const attach: LabelRefLite[] = []
  for (const name of desiredNames) {
    if (currentByName.has(name)) continue
    const existing = spaceByName.get(name)
    if (existing) {
      attach.push(existing)
    } else {
      create.push(name)
    }
  }

  const wanted = new Set(desiredNames)
  const detach = current.filter((label) => !wanted.has(label.name))

  return { create, attach, detach }
}
