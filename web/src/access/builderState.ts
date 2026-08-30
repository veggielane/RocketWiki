import type { RuleNode } from './ruleTypes'

/**
 * The rule builder's editable tree. Shaped like `RuleNode` (ruleTypes.ts)
 * plus a stable `id` per node for React keys and targeted edits, and — this
 * is the important difference — permissive enough to hold a condition
 * *mid-edit* (an empty group name while the admin is still typing, an attr
 * condition with zero values yet). `validateBuilderState` below is the one
 * place that decides whether a tree is complete enough to become a real
 * `RuleNode` and get serialized; nothing else in this module or the UI
 * components should attempt to call `ruleSerializer.serializeRuleNode`
 * directly on unvalidated state; **that's** how the builder stays incapable
 * of emitting anything the backend would reject.
 */
export interface AllOfBuilderNode {
  id: string
  kind: 'allOf'
  children: BuilderNode[]
}

export interface AnyOfBuilderNode {
  id: string
  kind: 'anyOf'
  children: BuilderNode[]
}

export interface EveryoneBuilderNode {
  id: string
  kind: 'everyone'
}

export interface GroupBuilderNode {
  id: string
  kind: 'group'
  group: string
}

export interface UserBuilderNode {
  id: string
  kind: 'user'
  userId: string
}

export interface AttrBuilderNode {
  id: string
  kind: 'attr'
  attribute: string
  in: string[]
}

export type CombinatorBuilderNode = AllOfBuilderNode | AnyOfBuilderNode
export type BuilderNode = CombinatorBuilderNode | EveryoneBuilderNode | GroupBuilderNode | UserBuilderNode | AttrBuilderNode

function isCombinator(node: BuilderNode): node is CombinatorBuilderNode {
  return node.kind === 'allOf' || node.kind === 'anyOf'
}

function createId(): string {
  return crypto.randomUUID()
}

// ---- Factories for new nodes (used by "add condition" / "add group" UI) ----

export function createEveryoneCondition(): EveryoneBuilderNode {
  return { id: createId(), kind: 'everyone' }
}

export function createGroupCondition(): GroupBuilderNode {
  return { id: createId(), kind: 'group', group: '' }
}

export function createUserCondition(): UserBuilderNode {
  return { id: createId(), kind: 'user', userId: '' }
}

export function createAttrCondition(): AttrBuilderNode {
  return { id: createId(), kind: 'attr', attribute: '', in: [] }
}

/** A freshly nested AND/OR group always starts with one (incomplete) child — an empty group never round-trips (see validation), so it can't be a valid leaf state on its own. */
export function createCombinatorNode(kind: 'allOf' | 'anyOf'): CombinatorBuilderNode {
  return { id: createId(), kind, children: [createGroupCondition()] }
}

// ---- Conversions to/from the wire type ----

export function ruleNodeToBuilderNode(node: RuleNode): BuilderNode {
  switch (node.kind) {
    case 'allOf':
      return { id: createId(), kind: 'allOf', children: node.children.map(ruleNodeToBuilderNode) }
    case 'anyOf':
      return { id: createId(), kind: 'anyOf', children: node.children.map(ruleNodeToBuilderNode) }
    case 'everyone':
      return { id: createId(), kind: 'everyone' }
    case 'group':
      return { id: createId(), kind: 'group', group: node.group }
    case 'user':
      return { id: createId(), kind: 'user', userId: node.userId }
    case 'attr':
      return { id: createId(), kind: 'attr', attribute: node.attribute, in: [...node.in] }
  }
}

function toRuleNode(node: BuilderNode): RuleNode {
  switch (node.kind) {
    case 'allOf':
      return { kind: 'allOf', children: node.children.map(toRuleNode) }
    case 'anyOf':
      return { kind: 'anyOf', children: node.children.map(toRuleNode) }
    case 'everyone':
      return { kind: 'everyone' }
    case 'group':
      return { kind: 'group', group: node.group }
    case 'user':
      return { kind: 'user', userId: node.userId }
    case 'attr':
      return { kind: 'attr', attribute: node.attribute, in: node.in }
  }
}

// ---- Validation — the emission gate ----

/**
 * Which control an issue belongs to.
 *
 * A tag, not a substring of the message. The editor used to decide where to
 * show an error by testing the English prose — `m.includes('value')` — and
 * `'Values must not be empty.'` starts with a capital V, so that issue matched
 * nothing and was never displayed at all: the rule stayed un-saveable with no
 * field marked and no sentence anywhere on screen. Prose is for reading;
 * routing needs something the compiler can check.
 */
export type IssueField = 'children' | 'group' | 'user' | 'attribute' | 'values'

export interface ValidationIssue {
  nodeId: string
  field: IssueField
  message: string
}

function collectIssues(node: BuilderNode, issues: ValidationIssue[]): void {
  switch (node.kind) {
    case 'allOf':
    case 'anyOf':
      if (node.children.length === 0) {
        issues.push({ nodeId: node.id, field: 'children', message: 'A group needs at least one condition.' })
      }
      for (const child of node.children) {
        collectIssues(child, issues)
      }
      break
    case 'everyone':
      break
    case 'group':
      if (node.group.trim().length === 0) {
        issues.push({ nodeId: node.id, field: 'group', message: 'Group name is required.' })
      }
      break
    case 'user':
      if (node.userId.trim().length === 0) {
        issues.push({ nodeId: node.id, field: 'user', message: 'User is required.' })
      }
      break
    case 'attr':
      if (node.attribute.trim().length === 0) {
        issues.push({ nodeId: node.id, field: 'attribute', message: 'Attribute is required.' })
      }
      if (node.in.length === 0) {
        issues.push({ nodeId: node.id, field: 'values', message: 'At least one value is required.' })
      } else if (node.in.some((v) => v.trim().length === 0)) {
        issues.push({ nodeId: node.id, field: 'values', message: 'Values must not be empty.' })
      }
      break
  }
}

export type ValidationResult = { valid: true; node: RuleNode } | { valid: false; issues: ValidationIssue[] }

/**
 * The single gate between builder state and the wire format. Never call
 * `serializeRuleNode` on anything but the `node` returned here — that's
 * what guarantees the builder can't emit a tree the backend would reject
 * (empty combinators, blank names, empty `in` arrays are exactly the cases
 * `RuleExpressionSerializer.Parse` throws on).
 */
export function validateBuilderState(root: BuilderNode): ValidationResult {
  const issues: ValidationIssue[] = []
  collectIssues(root, issues)
  if (issues.length > 0) {
    return { valid: false, issues }
  }
  return { valid: true, node: toRuleNode(root) }
}

// ---- Tree navigation and immutable edits ----

export function findNode(root: BuilderNode, targetId: string): BuilderNode | null {
  if (root.id === targetId) {
    return root
  }
  if (isCombinator(root)) {
    for (const child of root.children) {
      const found = findNode(child, targetId)
      if (found) return found
    }
  }
  return null
}

export function findParent(root: BuilderNode, targetId: string): CombinatorBuilderNode | null {
  if (isCombinator(root)) {
    if (root.children.some((c) => c.id === targetId)) {
      return root
    }
    for (const child of root.children) {
      const found = findParent(child, targetId)
      if (found) return found
    }
  }
  return null
}

/** A node can be removed unless it's the root, or removing it would leave its parent group empty (use "delete group" on the parent instead). */
export function canRemoveNode(root: BuilderNode, targetId: string): boolean {
  if (root.id === targetId) {
    return false
  }
  const parent = findParent(root, targetId)
  return parent !== null && parent.children.length > 1
}

export function updateNode(root: BuilderNode, targetId: string, updater: (node: BuilderNode) => BuilderNode): BuilderNode {
  if (root.id === targetId) {
    return updater(root)
  }
  if (isCombinator(root)) {
    return { ...root, children: root.children.map((c) => updateNode(c, targetId, updater)) }
  }
  return root
}

export function addChild(root: BuilderNode, parentId: string, child: BuilderNode): BuilderNode {
  return updateNode(root, parentId, (node) => {
    if (!isCombinator(node)) return node
    return { ...node, children: [...node.children, child] }
  })
}

export function removeNode(root: BuilderNode, targetId: string): BuilderNode {
  if (!isCombinator(root)) {
    return root
  }
  return {
    ...root,
    children: root.children.filter((c) => c.id !== targetId).map((c) => removeNode(c, targetId)),
  }
}

export function toggleCombinator(root: BuilderNode, groupId: string): BuilderNode {
  return updateNode(root, groupId, (node) => {
    if (node.kind === 'allOf') return { ...node, kind: 'anyOf' }
    if (node.kind === 'anyOf') return { ...node, kind: 'allOf' }
    return node
  })
}
