import { Box, Typography } from '@mui/material'

/**
 * Why the Insert button is disabled, said out loud.
 *
 * A greyed-out button with no explanation is the fence-insert dialogs' oldest
 * shared defect: three of them disable Insert on conditions that appear nowhere
 * on screen, so the only way to discover the rule is to type into fields until
 * the button lights up. `InsertPageListDialog` already narrates every invalid
 * state; this is the same treatment for the dialogs whose requirements are a
 * short list rather than a parsed query.
 *
 * Announced `polite`, not `assertive`: it changes on every keystroke, and an
 * assertive region would interrupt the author mid-word. The region itself is
 * always in the DOM — a live region that only appears when there is something
 * to say is a region screen readers were not watching when it changed.
 *
 * The message is what is MISSING, never a scolding about what is wrong: an
 * empty field on a freshly-opened dialog is incomplete, not an error, which is
 * why the fields themselves stay unflagged until they hold something invalid.
 */
export function InsertRequirements({ missing }: { missing: readonly string[] }) {
  return (
    <Box aria-live="polite">
      {missing.length > 0 && (
        <Typography variant="caption" color="error.main">
          Insert needs {joinWithAnd(missing)}.
        </Typography>
      )}
    </Box>
  )
}

/** "a", "a and b", "a, b and c" — the list read as a sentence, not as bullets. */
function joinWithAnd(items: readonly string[]): string {
  if (items.length <= 1) return items[0] ?? ''
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`
}
