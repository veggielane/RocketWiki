import type { AccessGate, SpaceRole } from '../../graphql/generated/graphql'
import { MARKING_UNAVAILABLE, NO_SPACE_ACCESS } from './protectedCopy'

/**
 * One evaluated gate, as the API's `GateResult` carries it — on a denial
 * (failing gates only), on the inspector (every gate, pass and fail), and on
 * a tree placeholder. Every detail field is nullable and each gate uses at
 * most a couple: the selector gate a category and value, the caveat gate the
 * releasable countries, a restriction its rule id and whether it was
 * inherited, the role gate the role required; the space-access and
 * marking-present gates carry nothing. Never a page id, title, timestamp,
 * expression or ancestor title — the projection is pinned server-side.
 *
 * No level, and nothing about what the reader "holds": the classification
 * is on no gate, because this deployment compares it against nobody.
 */
export interface GateCheck {
  gate: AccessGate
  passed: boolean
  category?: string | null
  value?: string | null
  countries?: readonly string[] | null
  ruleId?: string | null
  inherited?: boolean | null
  requiredRole?: SpaceRole | null
}

/** The short name of a gate, for a row label or a list heading. */
export function accessGateTitle(gate: AccessGate): string {
  switch (gate) {
    case 'SPACE_ACCESS':
      return 'Space access'
    case 'MARKING_UNAVAILABLE':
      return 'Marking'
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
export function describeAccessGate(check: GateCheck): string {
  return check.passed ? describePassing(check) : describeFailing(check)
}

function describeFailing(check: GateCheck): string {
  switch (check.gate) {
    case 'SPACE_ACCESS':
      return NO_SPACE_ACCESS
    case 'MARKING_UNAVAILABLE':
      // Nothing about the reader changes this one, so it says the same thing
      // to everyone: the page's marking row is gone and the server fails
      // closed on it.
      return MARKING_UNAVAILABLE
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

function describePassing(check: GateCheck): string {
  switch (check.gate) {
    case 'SPACE_ACCESS':
      return 'You hold an access grant in this space.'
    case 'MARKING_UNAVAILABLE':
      return "This page's marking is present."
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
