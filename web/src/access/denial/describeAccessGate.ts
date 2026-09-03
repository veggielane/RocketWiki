import type { AccessGate, ClassificationLevel, SpaceRole } from '../../graphql/generated/graphql'
import { NO_SPACE_ACCESS } from './protectedCopy'

/**
 * One evaluated gate, as the API's `GateResult` carries it — on a denial
 * (failing gates only), on the inspector (every gate, pass and fail), and on
 * a tree placeholder. Every detail field is nullable and each gate uses at
 * most a couple: the classification gate carries the required level's
 * display name, the selector gates a category and value, the caveat gate the
 * releasable countries, a restriction its rule id and whether it was
 * inherited, the role gate the role required. Never a page id, title,
 * timestamp, expression or ancestor title — the projection is pinned
 * server-side.
 */
export interface GateCheck {
  gate: AccessGate
  passed: boolean
  requiredLevel?: ClassificationLevel | null
  requiredLevelName?: string | null
  category?: string | null
  value?: string | null
  countries?: readonly string[] | null
  ruleId?: string | null
  inherited?: boolean | null
  requiredRole?: SpaceRole | null
}

/**
 * What the sentence may say about the READER. `heldLevelName` is the display
 * spelling of the caller's own clearance (`me.clearance` looked up in
 * `classificationScheme`); the SPA owns no spelling, so a caller that cannot
 * supply one gets the sentence without it rather than a wire name.
 */
export interface GateContext {
  heldLevelName?: string | null
}

/** The short name of a gate, for a row label or a list heading. */
export function accessGateTitle(gate: AccessGate): string {
  switch (gate) {
    case 'SPACE_ACCESS':
      return 'Space access'
    case 'CLASSIFICATION':
      return 'Clearance'
    case 'SELECTOR_ELIGIBILITY':
      return 'Eligibility'
    case 'SELECTOR_GRANT':
      return 'Selector grant'
    case 'NATIONAL_CAVEAT':
      return 'National caveat'
    case 'RESTRICTION':
      return 'Restriction'
    case 'REPLICA':
      return 'Replica'
    case 'ROLE':
      return 'Role'
    default: {
      // A gate this build has never heard of (a server ahead of this client)
      // still gets a row — its wire name is better than a hole.
      const unknown: never = gate
      return String(unknown)
    }
  }
}

/**
 * One sentence per gate, in either state. Failing sentences say what the
 * reader lacks; passing ones (the inspector's rows) say what they hold. Each
 * uses the detail its gate carries and degrades to a sentence without it
 * rather than printing "null" — the tree placeholder and the inspector's
 * rows carry less detail than a page-sized denial does.
 *
 * Countries are joined with `/` because that is how the server's formatter
 * lists them in a label (`AUS/NZ EYES ONLY`), and they arrive already
 * sorted; this does not re-order them.
 */
export function describeAccessGate(check: GateCheck, context: GateContext = {}): string {
  return check.passed ? describePassing(check, context) : describeFailing(check, context)
}

function describeFailing(check: GateCheck, context: GateContext): string {
  const held = context.heldLevelName
  switch (check.gate) {
    case 'SPACE_ACCESS':
      return NO_SPACE_ACCESS
    case 'CLASSIFICATION':
      if (check.requiredLevelName && held) return `Needs ${check.requiredLevelName} clearance; you hold ${held}.`
      if (check.requiredLevelName) return `Needs ${check.requiredLevelName} clearance.`
      return 'Above your clearance.'
    case 'SELECTOR_ELIGIBILITY':
      return check.category ? `Not eligible for ${check.category} material.` : 'Not eligible for material this page carries.'
    case 'SELECTOR_GRANT':
      if (check.value) return `${check.value} is not granted to you in this space.`
      if (check.category) return `No ${check.category} value is granted to you in this space.`
      return 'A selector value on this page is not granted to you in this space.'
    case 'NATIONAL_CAVEAT':
      return check.countries && check.countries.length > 0
        ? `Releasable to ${check.countries.join('/')} only.`
        : 'Not releasable to your nationality.'
    case 'RESTRICTION':
      return check.inherited ? 'Blocked by a restriction rule on an ancestor page.' : 'Blocked by a restriction rule.'
    case 'REPLICA':
      return 'This space is a read-only replica; nothing in it can be edited here.'
    case 'ROLE':
      if (check.requiredRole === 'EDITOR') return 'Needs the Editor role in this space.'
      if (check.requiredRole === 'SPACE_ADMIN') return 'Needs the Space admin role in this space.'
      return 'Needs a role grant in this space.'
    default: {
      const unknown: never = check.gate
      return `Blocked by ${String(unknown)}.`
    }
  }
}

function describePassing(check: GateCheck, context: GateContext): string {
  const held = context.heldLevelName
  switch (check.gate) {
    case 'SPACE_ACCESS':
      return 'You hold an access grant in this space.'
    case 'CLASSIFICATION':
      if (check.requiredLevelName && held) return `${check.requiredLevelName} clearance required; you hold ${held}.`
      if (check.requiredLevelName) return `${check.requiredLevelName} clearance required; you hold it.`
      return 'Your clearance covers this page.'
    case 'SELECTOR_ELIGIBILITY':
      return check.category ? `Eligible for ${check.category} material.` : 'Eligible for the material this page carries.'
    case 'SELECTOR_GRANT':
      return check.value
        ? `${check.value} is granted to you in this space.`
        : 'Every selector value on this page is granted to you in this space.'
    case 'NATIONAL_CAVEAT':
      return check.countries && check.countries.length > 0
        ? `Releasable to ${check.countries.join('/')}; you qualify.`
        : 'No national caveat withholds this page from you.'
    case 'RESTRICTION':
      return 'Every restriction on this page and its ancestors passes.'
    case 'REPLICA':
      return 'This space is not a replica.'
    case 'ROLE':
      return 'You hold a role in this space that allows this.'
    default: {
      const unknown: never = check.gate
      return `${String(unknown)} passes.`
    }
  }
}
