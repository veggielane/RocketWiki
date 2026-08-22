import { useState, type MouseEvent } from 'react'
import type { Editor } from '@tiptap/core'
import {
  Box,
  Divider,
  IconButton,
  Menu,
  MenuItem,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
} from '@mui/material'
import FormatBoldIcon from '@mui/icons-material/FormatBold'
import FormatItalicIcon from '@mui/icons-material/FormatItalic'
import StrikethroughSIcon from '@mui/icons-material/StrikethroughS'
import CodeIcon from '@mui/icons-material/Code'
import FormatQuoteIcon from '@mui/icons-material/FormatQuote'
import FormatListBulletedIcon from '@mui/icons-material/FormatListBulleted'
import FormatListNumberedIcon from '@mui/icons-material/FormatListNumbered'
import ChecklistIcon from '@mui/icons-material/Checklist'
import TableChartOutlinedIcon from '@mui/icons-material/TableChartOutlined'
import LinkIcon from '@mui/icons-material/Link'
import HorizontalRuleIcon from '@mui/icons-material/HorizontalRule'
import CampaignOutlinedIcon from '@mui/icons-material/CampaignOutlined'
import CodeOffIcon from '@mui/icons-material/DataObject'
import type { CalloutType } from './nodes/Callout'
import { CALLOUT_TYPES } from './nodes/Callout'

export function EditorToolbar({ editor }: { editor: Editor | null }) {
  const [calloutMenuAnchor, setCalloutMenuAnchor] = useState<HTMLElement | null>(null)

  if (!editor) {
    return null
  }

  const headingValue = [1, 2, 3].find((level) => editor.isActive('heading', { level })) ?? 0

  const insertCallout = (calloutType: CalloutType) => {
    editor.chain().focus().toggleWrap('callout', { calloutType }).run()
    setCalloutMenuAnchor(null)
  }

  const insertLink = () => {
    const href = window.prompt('Link URL (https://…)')
    if (!href) return
    editor.chain().focus().extendMarkRange('link').setLink({ href }).run()
  }

  const insertTable = () => {
    editor.chain().focus().insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run()
  }

  return (
    <Box
      sx={{
        display: 'flex',
        flexWrap: 'wrap',
        alignItems: 'center',
        gap: 0.5,
        p: 0.5,
        borderBottom: 1,
        borderColor: 'divider',
      }}
      role="toolbar"
      aria-label="Formatting"
    >
      <ToggleButtonGroup
        size="small"
        exclusive
        value={headingValue}
        onChange={(_e: MouseEvent<HTMLElement>, level: number | null) => {
          if (level === null || level === 0) {
            editor.chain().focus().setParagraph().run()
          } else {
            editor
              .chain()
              .focus()
              .toggleHeading({ level: level as 1 | 2 | 3 })
              .run()
          }
        }}
        aria-label="Heading level"
      >
        <ToggleButton value={1} aria-label="Heading 1">
          H1
        </ToggleButton>
        <ToggleButton value={2} aria-label="Heading 2">
          H2
        </ToggleButton>
        <ToggleButton value={3} aria-label="Heading 3">
          H3
        </ToggleButton>
      </ToggleButtonGroup>

      <Divider orientation="vertical" flexItem sx={{ mx: 0.5 }} />

      <Tooltip title="Bold">
        <IconButton
          size="small"
          color={editor.isActive('bold') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleBold().run()}
          aria-label="Bold"
          aria-pressed={editor.isActive('bold')}
        >
          <FormatBoldIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Italic">
        <IconButton
          size="small"
          color={editor.isActive('italic') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleItalic().run()}
          aria-label="Italic"
          aria-pressed={editor.isActive('italic')}
        >
          <FormatItalicIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Strikethrough">
        <IconButton
          size="small"
          color={editor.isActive('strike') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleStrike().run()}
          aria-label="Strikethrough"
          aria-pressed={editor.isActive('strike')}
        >
          <StrikethroughSIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Inline code">
        <IconButton
          size="small"
          color={editor.isActive('code') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleCode().run()}
          aria-label="Inline code"
          aria-pressed={editor.isActive('code')}
        >
          <CodeIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Link">
        <IconButton size="small" onClick={insertLink} aria-label="Insert link">
          <LinkIcon fontSize="small" />
        </IconButton>
      </Tooltip>

      <Divider orientation="vertical" flexItem sx={{ mx: 0.5 }} />

      <Tooltip title="Bulleted list">
        <IconButton
          size="small"
          color={editor.isActive('bulletList') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleBulletList().run()}
          aria-label="Bulleted list"
          aria-pressed={editor.isActive('bulletList')}
        >
          <FormatListBulletedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Numbered list">
        <IconButton
          size="small"
          color={editor.isActive('orderedList') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleOrderedList().run()}
          aria-label="Numbered list"
          aria-pressed={editor.isActive('orderedList')}
        >
          <FormatListNumberedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Task list">
        <IconButton
          size="small"
          color={editor.isActive('taskList') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleTaskList().run()}
          aria-label="Task list"
          aria-pressed={editor.isActive('taskList')}
        >
          <ChecklistIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Quote">
        <IconButton
          size="small"
          color={editor.isActive('blockquote') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleBlockquote().run()}
          aria-label="Quote"
          aria-pressed={editor.isActive('blockquote')}
        >
          <FormatQuoteIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Code block">
        <IconButton
          size="small"
          color={editor.isActive('codeBlock') ? 'primary' : 'default'}
          onClick={() => editor.chain().focus().toggleCodeBlock().run()}
          aria-label="Code block"
          aria-pressed={editor.isActive('codeBlock')}
        >
          <CodeOffIcon fontSize="small" />
        </IconButton>
      </Tooltip>

      <Divider orientation="vertical" flexItem sx={{ mx: 0.5 }} />

      <Tooltip title="Table">
        <IconButton size="small" onClick={insertTable} aria-label="Insert table">
          <TableChartOutlinedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title="Callout">
        <IconButton
          size="small"
          onClick={(e) => setCalloutMenuAnchor(e.currentTarget)}
          aria-label="Insert callout"
          aria-haspopup="menu"
        >
          <CampaignOutlinedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Menu anchorEl={calloutMenuAnchor} open={Boolean(calloutMenuAnchor)} onClose={() => setCalloutMenuAnchor(null)}>
        {CALLOUT_TYPES.map((type) => (
          <MenuItem key={type} onClick={() => insertCallout(type)}>
            {type[0]!.toUpperCase() + type.slice(1)}
          </MenuItem>
        ))}
      </Menu>
      <Tooltip title="Horizontal rule">
        <IconButton
          size="small"
          onClick={() => editor.chain().focus().setHorizontalRule().run()}
          aria-label="Horizontal rule"
        >
          <HorizontalRuleIcon fontSize="small" />
        </IconButton>
      </Tooltip>
    </Box>
  )
}
