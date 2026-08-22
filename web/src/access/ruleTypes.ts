/**
 * Mirrors `RocketWiki.Core.Access.RuleNode` (see
 * `src/RocketWiki.Core/Access/RuleNode.cs`) exactly. design.md §6.3: no NOT,
 * no deny rules — allow-list thinking only. Every shape here corresponds to
 * exactly one JSON shape the backend's `RuleExpressionSerializer` accepts;
 * see `ruleSerializer.ts`.
 */
export type RuleNode = AllOfNode | AnyOfNode | EveryoneCondition | GroupCondition | UserCondition | AttrCondition

export interface AllOfNode {
  kind: 'allOf'
  children: RuleNode[]
}

export interface AnyOfNode {
  kind: 'anyOf'
  children: RuleNode[]
}

/** Matches any authenticated principal. */
export interface EveryoneCondition {
  kind: 'everyone'
}

/** Matches if the principal is a member of `group`. */
export interface GroupCondition {
  kind: 'group'
  group: string
}

/** Matches if the principal's user id equals `userId`. */
export interface UserCondition {
  kind: 'user'
  userId: string
}

/**
 * Matches if the principal holds `attribute` and at least one of its values
 * is in `in`. A principal missing the attribute never matches (fail closed).
 */
export interface AttrCondition {
  kind: 'attr'
  attribute: string
  in: string[]
}

export function allOf(children: RuleNode[]): AllOfNode {
  return { kind: 'allOf', children }
}

export function anyOf(children: RuleNode[]): AnyOfNode {
  return { kind: 'anyOf', children }
}

export function everyone(): EveryoneCondition {
  return { kind: 'everyone' }
}

export function group(name: string): GroupCondition {
  return { kind: 'group', group: name }
}

export function user(userId: string): UserCondition {
  return { kind: 'user', userId }
}

export function attr(attribute: string, values: string[]): AttrCondition {
  return { kind: 'attr', attribute, in: values }
}
