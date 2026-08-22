import { useEffect, useMemo, useState } from 'react'
import { Alert, Box, Stack, Typography } from '@mui/material'
import type { AttributeOption } from './attributeOption'
import {
  addChild,
  removeNode,
  ruleNodeToBuilderNode,
  toggleCombinator,
  updateNode,
  validateBuilderState,
  type BuilderNode,
  type ValidationResult,
} from './builderState'
import { RuleNodeEditor } from './RuleNodeEditor'
import { RuleExpressionSummary } from './RuleExpressionSummary'
import type { RuleNode } from './ruleTypes'

export interface RuleBuilderProps {
  /** Seeds the builder once on mount — this is an uncontrolled component, like `RichTextEditor`. */
  initialValue: RuleNode
  onChange: (result: ValidationResult) => void
  /** Known Keycloak group paths (design.md §6.6 — accumulated from observed logins, freeSolo since a new one can be typed). */
  groups: string[]
  /** The attribute registry (design.md §6.2), for the attr-condition picker. */
  attributes: AttributeOption[]
}

/**
 * Visual AND/OR rule editor (design.md §6.3, §6.6). The one invariant that
 * matters more than anything else here: this component must never be able
 * to hand `onChange` a `RuleNode` the backend's
 * `RuleExpressionSerializer.Parse` would reject. That's enforced by routing
 * every change through `validateBuilderState` (builderState.ts) rather than
 * serializing raw builder state directly — see `ruleSerializer.test.ts` and
 * `builderState.test.ts` for the contract tests against the backend's own
 * shape.
 */
export function RuleBuilder({ initialValue, onChange, groups, attributes }: RuleBuilderProps) {
  const [root, setRoot] = useState<BuilderNode>(() => ruleNodeToBuilderNode(initialValue))
  const result = useMemo(() => validateBuilderState(root), [root])

  useEffect(() => {
    onChange(result)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [result])

  const actions = {
    onUpdate: (id: string, updater: (node: BuilderNode) => BuilderNode) => setRoot((prev) => updateNode(prev, id, updater)),
    onRemove: (id: string) => setRoot((prev) => removeNode(prev, id)),
    onAddChild: (parentId: string, child: BuilderNode) => setRoot((prev) => addChild(prev, parentId, child)),
    onToggleCombinator: (id: string) => setRoot((prev) => toggleCombinator(prev, id)),
    onChangeKind: (id: string, kind: BuilderNode['kind']) =>
      setRoot((prev) =>
        updateNode(prev, id, (): BuilderNode => {
          switch (kind) {
            case 'everyone':
              return { id, kind: 'everyone' }
            case 'group':
              return { id, kind: 'group', group: '' }
            case 'user':
              return { id, kind: 'user', userId: '' }
            case 'attr':
              return { id, kind: 'attr', attribute: '', in: [] }
            case 'allOf':
            case 'anyOf':
              return { id, kind, children: [{ id: crypto.randomUUID(), kind: 'group', group: '' }] }
          }
        }),
      ),
  }

  return (
    <Stack spacing={2}>
      <RuleNodeEditor root={root} nodeId={root.id} issues={result.valid ? [] : result.issues} depth={0} groups={groups} attributes={attributes} {...actions} />

      {!result.valid && (
        <Alert severity="warning" variant="outlined">
          This rule isn't complete yet — {result.issues.length} thing{result.issues.length === 1 ? '' : 's'} to fix
          before it can be saved.
        </Alert>
      )}

      <Box>
        <Typography variant="caption" color="text.secondary">
          Preview
        </Typography>
        {result.valid ? (
          <RuleExpressionSummary node={result.node} />
        ) : (
          <Typography variant="body2" color="text.secondary" sx={{ fontStyle: 'italic' }}>
            Fix the highlighted conditions to see a preview.
          </Typography>
        )}
      </Box>
    </Stack>
  )
}
