import type { RuleNode } from './ruleTypes'

/**
 * Mirrors `RocketWiki.Core.Access.MalformedRuleExpressionException`. design.md
 * §6.3: "a malformed or unevaluable rule denies access and logs an error" —
 * this is why the builder must never be able to produce a tree that throws
 * here (see `builderState.ts`'s `validateBuilderState`), and why loading an
 * externally-supplied `ExpressionJson` must not crash the admin UI.
 */
export class MalformedRuleExpressionError extends Error {}

/**
 * The JSON contract with the backend. Mirrors
 * `RocketWiki.Core.Access.RuleExpressionSerializer.Serialize` — see that
 * file's canonical definition. Key order matches it exactly (not required
 * for interop, since the backend's parser only checks key *sets*, but kept
 * identical so this reads as one contract, not a coincidentally-compatible
 * one).
 */
export function serializeRuleNode(node: RuleNode): string {
  return JSON.stringify(toPlainJson(node))
}

function toPlainJson(node: RuleNode): unknown {
  switch (node.kind) {
    case 'allOf':
      return { allOf: node.children.map(toPlainJson) }
    case 'anyOf':
      return { anyOf: node.children.map(toPlainJson) }
    case 'everyone':
      return { everyone: true }
    case 'group':
      return { group: node.group }
    case 'user':
      return { user: node.userId }
    case 'attr':
      return { attr: node.attribute, in: node.in }
  }
}

/**
 * Mirrors `RuleExpressionSerializer.Parse` exactly: validation is holistic —
 * a document parses only if every node in the tree is one of the recognized
 * shapes with *exactly* its expected key set, non-empty combinators, and
 * non-blank strings. Any deviation throws, matching the backend's "no
 * partially valid tree" rule.
 */
export function parseRuleNode(json: string): RuleNode {
  if (json.trim().length === 0) {
    throw new MalformedRuleExpressionError('Expression JSON is empty.')
  }

  let parsed: unknown
  try {
    parsed = JSON.parse(json)
  } catch (err) {
    throw new MalformedRuleExpressionError(`Invalid JSON: ${err instanceof Error ? err.message : String(err)}`)
  }

  return parseNode(parsed)
}

export function tryParseRuleNode(json: string): { node: RuleNode; error: null } | { node: null; error: string } {
  try {
    return { node: parseRuleNode(json), error: null }
  } catch (err) {
    if (err instanceof MalformedRuleExpressionError) {
      return { node: null, error: err.message }
    }
    throw err
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function keySetEquals(keys: string[], expected: string[]): boolean {
  return keys.length === expected.length && expected.every((k) => keys.includes(k))
}

function parseNode(value: unknown): RuleNode {
  if (!isPlainObject(value)) {
    throw new MalformedRuleExpressionError(
      `Expected a JSON object for a rule node, found ${value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value}.`,
    )
  }

  const keys = Object.keys(value)

  if (keySetEquals(keys, ['allOf'])) {
    return { kind: 'allOf', children: parseChildren(value.allOf, 'allOf') }
  }

  if (keySetEquals(keys, ['anyOf'])) {
    return { kind: 'anyOf', children: parseChildren(value.anyOf, 'anyOf') }
  }

  if (keySetEquals(keys, ['everyone'])) {
    if (value.everyone !== true) {
      throw new MalformedRuleExpressionError('"everyone" must be the boolean `true`.')
    }
    return { kind: 'everyone' }
  }

  if (keySetEquals(keys, ['group'])) {
    return { kind: 'group', group: requireNonEmptyString(value.group, 'group') }
  }

  if (keySetEquals(keys, ['user'])) {
    return { kind: 'user', userId: requireNonEmptyString(value.user, 'user') }
  }

  if (keySetEquals(keys, ['attr', 'in'])) {
    const attribute = requireNonEmptyString(value.attr, 'attr')
    if (!Array.isArray(value.in) || value.in.length === 0) {
      throw new MalformedRuleExpressionError('"in" must be a non-empty array of strings.')
    }
    const values = value.in.map((item) => requireNonEmptyString(item, 'in[]'))
    return { kind: 'attr', attribute, in: values }
  }

  throw new MalformedRuleExpressionError(`Unrecognized rule node shape with keys: ${[...keys].sort().join(', ')}.`)
}

function parseChildren(value: unknown, combinatorName: string): RuleNode[] {
  if (!Array.isArray(value) || value.length === 0) {
    throw new MalformedRuleExpressionError(`"${combinatorName}" must be a non-empty array.`)
  }
  return value.map(parseNode)
}

function requireNonEmptyString(value: unknown, fieldName: string): string {
  if (typeof value !== 'string') {
    throw new MalformedRuleExpressionError(`"${fieldName}" must be a string.`)
  }
  if (value.trim().length === 0) {
    throw new MalformedRuleExpressionError(`"${fieldName}" must not be empty.`)
  }
  return value
}
