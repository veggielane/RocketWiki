import { Box, Chip, Stack, Typography } from '@mui/material'
import type { RuleNode } from './ruleTypes'

/**
 * Read-only rendering of a `RuleNode` tree as readable text, e.g.
 * "engineering AND (nationality in NZ, US OR export-cleared)". Shared
 * between the rule builder's live preview and the permission inspector's
 * per-restriction display (PermissionInspector.tsx) — one renderer for a
 * rule expression, same spirit as the editor's "one renderer" rule for
 * Markdown.
 */
export function RuleExpressionSummary({ node, depth = 0 }: { node: RuleNode; depth?: number }) {
  switch (node.kind) {
    case 'everyone':
      return <Chip size="small" label="Everyone" />

    case 'group':
      return <Chip size="small" label={`group: ${node.group}`} />

    case 'user':
      return <Chip size="small" label={`user: ${node.userId}`} />

    case 'attr':
      return <Chip size="small" label={`${node.attribute} in [${node.in.join(', ')}]`} />

    case 'allOf':
    case 'anyOf': {
      const joiner = node.kind === 'allOf' ? 'AND' : 'OR'
      return (
        <Stack
          spacing={0.5}
          sx={{
            pl: depth > 0 ? 1.5 : 0,
            borderLeft: depth > 0 ? '2px solid' : 'none',
            borderColor: 'divider',
          }}
        >
          {node.children.map((child, i) => (
            <Box key={i}>
              {i > 0 && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', fontWeight: 600 }}>
                  {joiner}
                </Typography>
              )}
              <RuleExpressionSummary node={child} depth={depth + 1} />
            </Box>
          ))}
        </Stack>
      )
    }
  }
}
