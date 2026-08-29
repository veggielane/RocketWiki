import { useState, type ReactNode } from 'react'
import {
  Autocomplete,
  Box,
  Button,
  Chip,
  FormControl,
  FormHelperText,
  IconButton,
  InputLabel,
  Menu,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  type SelectChangeEvent,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlined'
import AddIcon from '@mui/icons-material/Add'
import type { AttributeOption } from './attributeOption'
import {
  canRemoveNode,
  createAttrCondition,
  createCombinatorNode,
  createEveryoneCondition,
  createGroupCondition,
  createUserCondition,
  findNode,
  type BuilderNode,
  type ValidationIssue,
} from './builderState'

const NODE_KIND_LABELS: Record<BuilderNode['kind'], string> = {
  everyone: 'Everyone (any signed-in user)',
  group: 'Group',
  user: 'User',
  attr: 'Attribute',
  allOf: 'All of (AND)',
  anyOf: 'Any of (OR)',
}

export interface RuleNodeEditorActions {
  onUpdate: (id: string, updater: (node: BuilderNode) => BuilderNode) => void
  onRemove: (id: string) => void
  onAddChild: (parentId: string, child: BuilderNode) => void
  onToggleCombinator: (id: string) => void
  onChangeKind: (id: string, kind: BuilderNode['kind']) => void
}

interface RuleNodeEditorProps extends RuleNodeEditorActions {
  root: BuilderNode
  nodeId: string
  issues: ValidationIssue[]
  groups: string[]
  attributes: AttributeOption[]
  depth: number
}

function issuesFor(issues: ValidationIssue[], nodeId: string): string[] {
  return issues.filter((i) => i.nodeId === nodeId).map((i) => i.message)
}

/**
 * One rule node, of any of the six shapes in `ruleTypes.ts`. Unified rather
 * than split into separate "group editor" / "condition row" components
 * because the backend allows a bare leaf as a complete top-level expression
 * (design.md §6.4's "open" space is just `{ everyone: true }` with no
 * wrapping `allOf`) — the type selector doubles as "convert this into a
 * group" for any node, root included.
 */
export function RuleNodeEditor(props: RuleNodeEditorProps) {
  const { root, nodeId, issues, onRemove, onChangeKind } = props
  const node = findNode(root, nodeId)
  if (!node) return null

  const canRemove = canRemoveNode(root, nodeId)
  const myIssues = issuesFor(issues, nodeId)

  const handleKindChange = (e: SelectChangeEvent<BuilderNode['kind']>) => {
    onChangeKind(nodeId, e.target.value as BuilderNode['kind'])
  }

  // A VISIBLE label, not just an `aria-label`. This is the most important
  // control in the row — it decides what kind of condition this is — and it sat
  // unlabelled directly beside a "Group" or "User ID" field that had one, so a
  // sighted user could not tell what the box selected without opening it.
  const kindSelect = (
    <FormControl size="small" sx={{ minWidth: 220 }}>
      <InputLabel id={`${nodeId}-kind-label`}>Condition</InputLabel>
      <Select
        labelId={`${nodeId}-kind-label`}
        label="Condition"
        value={node.kind}
        onChange={handleKindChange}
      >
        {(Object.keys(NODE_KIND_LABELS) as BuilderNode['kind'][]).map((kind) => (
          <MenuItem key={kind} value={kind}>
            {NODE_KIND_LABELS[kind]}
          </MenuItem>
        ))}
      </Select>
    </FormControl>
  )

  const removeButton = (
    <Tooltip
      title={canRemove ? 'Remove this condition' : 'A group needs at least one condition — remove the group instead'}
    >
      <span>
        <IconButton size="small" onClick={() => onRemove(nodeId)} disabled={!canRemove} aria-label="Remove condition">
          <DeleteOutlineIcon fontSize="small" />
        </IconButton>
      </span>
    </Tooltip>
  )

  if (node.kind === 'allOf' || node.kind === 'anyOf') {
    return (
      <RuleGroupBody
        {...props}
        node={node}
        myIssues={myIssues}
        kindSelect={kindSelect}
        removeButton={removeButton}
      />
    )
  }

  return <RuleLeafBody {...props} node={node} myIssues={myIssues} kindSelect={kindSelect} removeButton={removeButton} />
}

function RuleGroupBody({
  root,
  nodeId,
  node,
  depth,
  myIssues,
  kindSelect,
  removeButton,
  onAddChild,
  onToggleCombinator,
  ...rest
}: RuleNodeEditorProps & {
  node: Extract<BuilderNode, { kind: 'allOf' | 'anyOf' }>
  myIssues: string[]
  kindSelect: ReactNode
  removeButton: ReactNode
}) {
  const [addMenuAnchor, setAddMenuAnchor] = useState<HTMLElement | null>(null)

  const addLeaf = (factory: () => BuilderNode) => {
    onAddChild(nodeId, factory())
    setAddMenuAnchor(null)
  }

  return (
    <Paper
      variant="outlined"
      sx={{ p: 1.5, borderLeft: 4, borderLeftColor: node.kind === 'allOf' ? 'primary.main' : 'secondary.main' }}
    >
      <Stack direction="row" spacing={1} useFlexGap sx={{ mb: 1, alignItems: 'center', flexWrap: 'wrap' }}>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={node.kind}
          onChange={(_e, value: 'allOf' | 'anyOf' | null) => {
            if (value && value !== node.kind) onToggleCombinator(nodeId)
          }}
          aria-label="Combinator"
          aria-describedby={myIssues.length > 0 ? `${nodeId}-issues` : undefined}
        >
          <ToggleButton value="allOf">AND (all of)</ToggleButton>
          <ToggleButton value="anyOf">OR (any of)</ToggleButton>
        </ToggleButtonGroup>
        {depth > 0 && kindSelect}
        <Box sx={{ flexGrow: 1 }} />
        {depth > 0 && removeButton}
      </Stack>

      {myIssues.length > 0 && (
        // `id` + `role="alert"`: the group's errors used to be a bare helper
        // line associated with nothing, so a screen reader met them only by
        // walking into them. The group's own controls point at it below.
        <FormHelperText error id={`${nodeId}-issues`} role="alert" sx={{ mb: 1 }}>
          {myIssues.join(' ')}
        </FormHelperText>
      )}

      <Stack spacing={1} sx={{ pl: 2, borderLeft: '2px dashed', borderColor: 'divider' }}>
        {node.children.map((child) => (
          <RuleNodeEditor
            key={child.id}
            {...rest}
            root={root}
            onAddChild={onAddChild}
            onToggleCombinator={onToggleCombinator}
            nodeId={child.id}
            depth={depth + 1}
          />
        ))}
      </Stack>

      <Stack direction="row" spacing={1} sx={{ mt: 1 }}>
        <Button size="small" startIcon={<AddIcon />} onClick={(e) => setAddMenuAnchor(e.currentTarget)}>
          Add condition
        </Button>
        <Menu anchorEl={addMenuAnchor} open={Boolean(addMenuAnchor)} onClose={() => setAddMenuAnchor(null)}>
          <MenuItem onClick={() => addLeaf(createGroupCondition)}>Group</MenuItem>
          <MenuItem onClick={() => addLeaf(createUserCondition)}>User</MenuItem>
          <MenuItem onClick={() => addLeaf(createAttrCondition)}>Attribute</MenuItem>
          <MenuItem onClick={() => addLeaf(createEveryoneCondition)}>Everyone</MenuItem>
        </Menu>
        <Button size="small" startIcon={<AddIcon />} onClick={() => onAddChild(nodeId, createCombinatorNode('anyOf'))}>
          Add nested group
        </Button>
      </Stack>
    </Paper>
  )
}

function RuleLeafBody({
  nodeId,
  node,
  myIssues,
  kindSelect,
  removeButton,
  groups,
  attributes,
  onUpdate,
}: RuleNodeEditorProps & {
  node: Extract<BuilderNode, { kind: 'everyone' | 'group' | 'user' | 'attr' }>
  myIssues: string[]
  kindSelect: ReactNode
  removeButton: ReactNode
}) {
  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
      {kindSelect}

      {node.kind === 'everyone' && <Box sx={{ flexGrow: 1 }} />}

      {node.kind === 'group' && (
        <Autocomplete
          freeSolo
          size="small"
          options={groups}
          value={node.group}
          onChange={(_e, value) => onUpdate(nodeId, (n) => (n.kind === 'group' ? { ...n, group: value ?? '' } : n))}
          onInputChange={(_e, value) => onUpdate(nodeId, (n) => (n.kind === 'group' ? { ...n, group: value } : n))}
          renderInput={(params) => (
            // Every issue, not just `myIssues[0]` — a field with two problems
            // reported one, so fixing it surfaced the next one as if it were new.
            <TextField {...params} label="Group" error={myIssues.length > 0} helperText={myIssues.join(' ')} />
          )}
          sx={{ flexGrow: 1, maxWidth: 320, minWidth: 240 }}
        />
      )}

      {node.kind === 'user' && (
        <TextField
          size="small"
          label="User ID"
          value={node.userId}
          onChange={(e) => onUpdate(nodeId, (n) => (n.kind === 'user' ? { ...n, userId: e.target.value } : n))}
          error={myIssues.length > 0}
          helperText={myIssues.length > 0 ? myIssues.join(' ') : 'No user directory yet — enter the subject id.'}
          sx={{ flexGrow: 1, maxWidth: 320 }}
        />
      )}

      {node.kind === 'attr' && (
        <>
          <FormControl size="small" error={myIssues.some((m) => m.includes('Attribute'))} sx={{ minWidth: 200 }}>
            <InputLabel id={`${nodeId}-attribute-label`}>Attribute</InputLabel>
            <Select
              labelId={`${nodeId}-attribute-label`}
              label="Attribute"
              displayEmpty
              value={node.attribute}
              onChange={(e) =>
                onUpdate(nodeId, (n) => (n.kind === 'attr' ? { ...n, attribute: e.target.value, in: [] } : n))
              }
            >
              <MenuItem value="" disabled>
                Select attribute…
              </MenuItem>
              {attributes.map((a) => (
                <MenuItem key={a.key} value={a.key}>
                  {a.displayName ?? a.key}
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          <Autocomplete
            multiple
            size="small"
            options={attributes.find((a) => a.key === node.attribute)?.allowedValues ?? []}
            value={node.in}
            onChange={(_e, values) => onUpdate(nodeId, (n) => (n.kind === 'attr' ? { ...n, in: values } : n))}
            disabled={!node.attribute}
            renderValue={(value, getItemProps) =>
              value.map((option, index) => {
                const { key, ...itemProps } = getItemProps({ index })
                return <Chip size="small" label={option} {...itemProps} key={key} />
              })
            }
            renderInput={(params) => (
              <TextField
                {...params}
                label="In"
                error={myIssues.some((m) => m.includes('value'))}
                helperText={myIssues.find((m) => m.includes('value'))}
              />
            )}
            sx={{ flexGrow: 1, minWidth: 240 }}
          />
        </>
      )}

      {removeButton}
    </Stack>
  )
}
