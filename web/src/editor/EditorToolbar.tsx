import { useState, type MouseEvent } from 'react'
import type { Editor } from '@tiptap/core'
import { useEditorState } from '@tiptap/react'
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
import AccountTreeOutlinedIcon from '@mui/icons-material/AccountTreeOutlined'
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined'
import FormatListBulletedAddIcon from '@mui/icons-material/PlaylistAddOutlined'
import CallMergeIcon from '@mui/icons-material/CallMerge'
import CallSplitIcon from '@mui/icons-material/CallSplit'
import FormatAlignLeftIcon from '@mui/icons-material/FormatAlignLeft'
import FormatAlignCenterIcon from '@mui/icons-material/FormatAlignCenter'
import FormatAlignRightIcon from '@mui/icons-material/FormatAlignRight'
import { currentCellAlign, mergeWouldCrossHeaderBoundary, type TableColumnAlign } from './tableEditing'
import type { CalloutType } from './nodes/Callout'
import { CALLOUT_TYPES } from './nodes/Callout'
import { useGitLabStatusQuery } from '../graphql/generated/graphql'
import { InsertGitLabIssueLinkDialog } from './gitlab/InsertGitLabIssueLinkDialog'
import { InsertGitLabFileDialog } from './gitlab/InsertGitLabFileDialog'
import { InsertGitLabIssuesDialog } from './gitlab/InsertGitLabIssuesDialog'
import { buildFileFenceBody, buildIssuesFenceBody, type GitLabFileRef, type GitLabIssuesSpec } from '../gitlab/fenceBody'
import type { GitLabIssueRef } from '../gitlab/issueScheme'
import { InsertPageListDialog } from './pagelist/InsertPageListDialog'
import { InsertFormDialog } from './forms/InsertFormDialog'
import DynamicFormOutlinedIcon from '@mui/icons-material/DynamicFormOutlined'
import { buildPageListFenceBody, type PageListSpec } from '../pagelist/fenceBody'
import { EmojiPickerButton } from './emoji/EmojiPickerButton'

export function EditorToolbar({ editor }: { editor: Editor | null }) {
  const [calloutMenuAnchor, setCalloutMenuAnchor] = useState<HTMLElement | null>(null)
  const [diagramMenuAnchor, setDiagramMenuAnchor] = useState<HTMLElement | null>(null)
  const [gitlabMenuAnchor, setGitlabMenuAnchor] = useState<HTMLElement | null>(null)
  const [gitlabDialog, setGitlabDialog] = useState<'issue-link' | 'file' | 'issues' | null>(null)
  // Not behind any integration flag, unlike the GitLab menu: RQL (design.md
  // §22) is this instance's own query language, always present.
  const [pageListDialogOpen, setPageListDialogOpen] = useState(false)
  const [formDialogOpen, setFormDialogOpen] = useState(false)
  // §15/§18 fail-closed extends to UI affordances: no GitLab:BaseUrl means
  // the feature is absent, so the whole GitLab menu is hidden, not disabled.
  const [{ data: gitlabStatusData }] = useGitLabStatusQuery()
  const gitlabConfigured = gitlabStatusData?.gitlabStatus.configured === true

  // Contextual table controls. useEditor (v3) doesn't re-render on
  // transactions, so this subscribes explicitly to exactly the state the
  // table section needs; null while the selection is outside any table,
  // which hides the section entirely (absent, not disabled — same posture
  // as the GitLab menu).
  const tableState = useEditorState({
    editor,
    selector: ({ editor: e }) => {
      if (!e || !e.isActive('table')) return null
      return {
        canMerge: e.can().mergeTableCells(),
        canSplit: e.can().splitCell(),
        // Distinguished from plain "can't merge" so the tooltip can say why.
        mergeCrossesHeader: mergeWouldCrossHeaderBoundary(e.state),
        align: currentCellAlign(e.state),
      }
    },
  })

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

  const insertMermaid = () => {
    // A mermaid diagram IS a code block with language `mermaid` — the node
    // view adds the live preview (nodes/CodeBlockView.tsx); markdown stays
    // a plain fence.
    editor.chain().focus().setCodeBlock({ language: 'mermaid' }).run()
    setDiagramMenuAnchor(null)
  }

  const insertDrawio = () => {
    editor.chain().focus().insertContent({ type: 'drawioDiagram' }).run()
    setDiagramMenuAnchor(null)
  }

  const openGitlabDialog = (dialog: 'issue-link' | 'file' | 'issues') => {
    setGitlabDialog(dialog)
    setGitlabMenuAnchor(null)
  }

  const selectionText = () => {
    const { from, to } = editor.state.selection
    return editor.state.doc.textBetween(from, to, ' ')
  }

  const insertGitlabIssueLink = (ref: GitLabIssueRef, text: string) => {
    // Replaces the selection (if any) with the linked text; the mark is
    // `inclusive: false`, so typing after it doesn't extend the link.
    editor
      .chain()
      .focus()
      .insertContent({
        type: 'text',
        text,
        marks: [{ type: 'gitlabIssueLink', attrs: { project: ref.project, iid: ref.iid } }],
      })
      .run()
  }

  /**
   * Every reserved fence is inserted the same way — as an ordinary
   * `codeBlock` carrying a language. That is the whole storage design
   * (design.md §22, §18): no bespoke node, so the Markdown pipeline needs no
   * branch and the round trip is byte-exact for free.
   */
  const insertFence = (
    language: 'gitlab-file' | 'gitlab-issues' | 'page-list' | 'form-definition' | 'form-list',
    body: string,
  ) => {
    editor
      .chain()
      .focus()
      .insertContent({
        type: 'codeBlock',
        attrs: { language },
        content: [{ type: 'text', text: body }],
      })
      .run()
  }

  const insertGitlabFile = (ref: GitLabFileRef) => insertFence('gitlab-file', buildFileFenceBody(ref))
  const insertGitlabIssues = (spec: GitLabIssuesSpec) => insertFence('gitlab-issues', buildIssuesFenceBody(spec))
  const insertPageList = (spec: PageListSpec) => insertFence('page-list', buildPageListFenceBody(spec))

  // Two fences in one action when the author asked for the table as well. Inserted
  // in order, so the definition sits above the records it describes — which is the
  // reading order, and the order the author was thinking in.
  const insertForm = (fences: { language: 'form-definition' | 'form-list'; body: string }[]) => {
    for (const fence of fences) {
      insertFence(fence.language, fence.body)
    }
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
      {tableState && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }} role="group" aria-label="Table cell controls">
          <Tooltip
            title={
              tableState.mergeCrossesHeader
                ? 'Header and body cells cannot merge — the merge would not survive saving'
                : 'Merge cells'
            }
          >
            {/* span: MUI Tooltips need an enabled child to anchor events on */}
            <span>
              <IconButton
                size="small"
                onClick={() => editor.chain().focus().mergeTableCells().run()}
                disabled={!tableState.canMerge}
                aria-label="Merge cells"
              >
                <CallMergeIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <Tooltip title="Split cell">
            <span>
              <IconButton
                size="small"
                onClick={() => editor.chain().focus().splitCell().run()}
                disabled={!tableState.canSplit}
                aria-label="Split cell"
              >
                <CallSplitIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={tableState.align ?? ''}
            onChange={(_e: MouseEvent<HTMLElement>, align: TableColumnAlign | '' | null) => {
              // Clicking the active toggle yields null — clears back to the
              // unaligned `---` column.
              editor
                .chain()
                .focus()
                .setTableColumnAlign(align === '' || align === null ? null : align)
                .run()
            }}
            aria-label="Column alignment"
          >
            <ToggleButton value="left" aria-label="Align column left">
              <FormatAlignLeftIcon fontSize="small" />
            </ToggleButton>
            <ToggleButton value="center" aria-label="Align column center">
              <FormatAlignCenterIcon fontSize="small" />
            </ToggleButton>
            <ToggleButton value="right" aria-label="Align column right">
              <FormatAlignRightIcon fontSize="small" />
            </ToggleButton>
          </ToggleButtonGroup>
        </Box>
      )}
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
      <Tooltip title="Diagram">
        <IconButton
          size="small"
          onClick={(e) => setDiagramMenuAnchor(e.currentTarget)}
          aria-label="Insert diagram"
          aria-haspopup="menu"
        >
          <AccountTreeOutlinedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Menu anchorEl={diagramMenuAnchor} open={Boolean(diagramMenuAnchor)} onClose={() => setDiagramMenuAnchor(null)}>
        <MenuItem onClick={insertMermaid}>Mermaid diagram</MenuItem>
        <MenuItem onClick={insertDrawio}>draw.io diagram</MenuItem>
      </Menu>
      <Tooltip title="Page list">
        <IconButton
          size="small"
          onClick={() => setPageListDialogOpen(true)}
          aria-label="Insert page list"
          aria-haspopup="dialog"
        >
          <FormatListBulletedAddIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <InsertPageListDialog
        open={pageListDialogOpen}
        onClose={() => setPageListDialogOpen(false)}
        onInsert={insertPageList}
      />
      <Tooltip title="Form">
        <IconButton
          size="small"
          onClick={() => setFormDialogOpen(true)}
          aria-label="Insert form"
          aria-haspopup="dialog"
        >
          <DynamicFormOutlinedIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <InsertFormDialog
        open={formDialogOpen}
        onClose={() => setFormDialogOpen(false)}
        onInsert={insertForm}
      />
      {/* Hidden when the registry is empty (EmojiPickerButton) — same
          absent-not-disabled posture as the GitLab menu below. */}
      <EmojiPickerButton editor={editor} />
      {gitlabConfigured && (
        <>
          <Tooltip title="GitLab">
            <IconButton
              size="small"
              onClick={(e) => setGitlabMenuAnchor(e.currentTarget)}
              aria-label="Insert GitLab content"
              aria-haspopup="menu"
            >
              <BugReportOutlinedIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Menu anchorEl={gitlabMenuAnchor} open={Boolean(gitlabMenuAnchor)} onClose={() => setGitlabMenuAnchor(null)}>
            <MenuItem onClick={() => openGitlabDialog('issue-link')}>Issue link</MenuItem>
            <MenuItem onClick={() => openGitlabDialog('file')}>File embed</MenuItem>
            <MenuItem onClick={() => openGitlabDialog('issues')}>Issue list</MenuItem>
          </Menu>
          <InsertGitLabIssueLinkDialog
            open={gitlabDialog === 'issue-link'}
            initialText={gitlabDialog === 'issue-link' ? selectionText() : ''}
            onClose={() => setGitlabDialog(null)}
            onInsert={insertGitlabIssueLink}
          />
          <InsertGitLabFileDialog
            open={gitlabDialog === 'file'}
            onClose={() => setGitlabDialog(null)}
            onInsert={insertGitlabFile}
          />
          <InsertGitLabIssuesDialog
            open={gitlabDialog === 'issues'}
            onClose={() => setGitlabDialog(null)}
            onInsert={insertGitlabIssues}
          />
        </>
      )}
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
