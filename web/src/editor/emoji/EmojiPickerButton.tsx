import { useState } from 'react'
import type { Editor } from '@tiptap/core'
import { Box, IconButton, Popover, Tooltip } from '@mui/material'
import AddReactionOutlinedIcon from '@mui/icons-material/AddReactionOutlined'
import { useEmojiRegistrySnapshot } from '../../emoji/useEmojiRegistry'
import { EmojiImg } from '../../emoji/EmojiImg'

/**
 * Toolbar picker listing the whole custom-emoji registry (design.md §19).
 * Choosing one **inserts the literal `:name:` text** — same rule as the
 * `:` autocomplete: emojis are plain Markdown text end to end, rendered
 * only at view time by the decoration plugin. Hidden entirely when the
 * registry is empty — an instance with no custom emojis has no feature to
 * offer, the same absent-not-disabled posture as the GitLab menu.
 */
export function EmojiPickerButton({ editor }: { editor: Editor }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const registry = useEmojiRegistrySnapshot()

  if (registry.size === 0) {
    return null
  }

  const insert = (name: string) => {
    setAnchor(null)
    editor.chain().focus().insertContent(`:${name}:`).run()
  }

  return (
    <>
      <Tooltip title="Emoji">
        <IconButton size="small" onClick={(e) => setAnchor(e.currentTarget)} aria-label="Insert emoji" aria-haspopup="dialog">
          <AddReactionOutlinedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Popover
        open={Boolean(anchor)}
        anchorEl={anchor}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
      >
        <Box sx={{ p: 1, maxWidth: 320, maxHeight: 280, overflowY: 'auto' }} role="listbox" aria-label="Custom emojis">
          {[...registry.entries()]
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([name, etag]) => (
              <Tooltip key={name} title={`:${name}:`}>
                <IconButton size="small" onClick={() => insert(name)} aria-label={`Insert :${name}:`} role="option">
                  <EmojiImg name={name} etag={etag} size={22} />
                </IconButton>
              </Tooltip>
            ))}
        </Box>
      </Popover>
    </>
  )
}
