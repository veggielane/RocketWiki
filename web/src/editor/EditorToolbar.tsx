import { useRef, useState, type MouseEvent } from 'react'
import { getMarkRange, type Editor } from '@tiptap/core'
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
import CodeBlockIcon from '@mui/icons-material/DataObject'
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
import { InsertLinkDialog, type LinkTarget } from './InsertLinkDialog'
import { useRovingToolbar } from './useRovingToolbar'

/**
 * A stateful mark button. A `ToggleButton` rather than an `IconButton` tinted
 * `color="primary"`, because a tint is the whole of the on/off signal to a
 * sighted user and colour alone is a WCAG 1.4.1 failure — the same toolbar was
 * already using `ToggleButton`'s background fill for the heading and alignment
 * groups two centimetres away, so this is also the toolbar agreeing with
 * itself. `ToggleButton` sets `aria-pressed` from `selected`, so there is no
 * separate attribute to keep in step.
 */
function MarkToggle({
  label,
  active,
  onToggle,
  children,
}: {
  label: string
  active: boolean
  onToggle: () => void
  children: React.ReactNode
}) {
  return (
    <Tooltip title={label}>
      <ToggleButton
        size="small"
        value={label}
        selected={active}
        onChange={onToggle}
        aria-label={label}
        sx={{ border: 0, p: 0.75 }}
      >
        {children}
      </ToggleButton>
    </Tooltip>
  )
}

export function EditorToolbar({ editor }: { editor: Editor | null }) {
  const [calloutMenuAnchor, setCalloutMenuAnchor] = useState<HTMLElement | null>(null)
  const [diagramMenuAnchor, setDiagramMenuAnchor] = useState<HTMLElement | null>(null)
  const [gitlabMenuAnchor, setGitlabMenuAnchor] = useState<HTMLElement | null>(null)
  const [gitlabDialog, setGitlabDialog] = useState<'issue-link' | 'file' | 'issues' | null>(null)
  // Not behind any integration flag, unlike the GitLab menu: RQL (design.md
  // §22) is this instance's own query language, always present.
  const [pageListDialogOpen, setPageListDialogOpen] = useState(false)
  const [formDialogOpen, setFormDialogOpen] = useState(false)
  const [linkDialogOpen, setLinkDialogOpen] = useState(false)
  // §15/§18 fail-closed extends to UI affordances: no GitLab:BaseUrl means
  // the feature is absent, so the whole GitLab menu is hidden, not disabled.
  const [{ data: gitlabStatusData }] = useGitLabStatusQuery()
  const gitlabConfigured = gitlabStatusData?.gitlabStatus.configured === true

  const toolbarRef = useRef<HTMLDivElement>(null)
  const { onKeyDown } = useRovingToolbar(toolbarRef)

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

  /** The link under the cursor, if any — makes the dialog an edit rather than an insert. */
  const currentLink = ((): LinkTarget | null => {
    if (editor.isActive('pageLink')) {
      const pageId = editor.getAttributes('pageLink').pageId as string | undefined
      return pageId ? { kind: 'page', pageId } : null
    }
    if (editor.isActive('link')) {
      const href = editor.getAttributes('link').href as string | undefined
      return href ? { kind: 'external', href } : null
    }
    return null
  })()

  const selectionText = () => {
    const { from, to } = editor.state.selection
    return editor.state.doc.textBetween(from, to, ' ')
  }

  /**
   * Link text is the selection when there is one; for a bare caret sitting
   * inside a link it is that whole link's text, so editing one does not mean
   * retyping its label. `getMarkRange` reads the extent without dispatching a
   * transaction — the dialog must not move the user's selection just by opening.
   */
  const linkDialogText = () => {
    const { from, to, $from } = editor.state.selection
    if (from !== to) return selectionText()
    if (!currentLink) return ''
    const markType = editor.schema.marks[currentLink.kind === 'page' ? 'pageLink' : 'link']
    const range = markType ? getMarkRange($from, markType) : null
    return range ? editor.state.doc.textBetween(range.from, range.to, ' ') : ''
  }

  const applyLink = (target: LinkTarget, text: string) => {
    const markName = target.kind === 'page' ? 'pageLink' : 'link'
    const other = target.kind === 'page' ? 'link' : 'pageLink'
    // Both marks are cleared before the chosen one is set: switching a link from
    // external to internal must not leave the old mark stacked underneath, which
    // would serialize as two overlapping links.
    editor
      .chain()
      .focus()
      .extendMarkRange(markName)
      .extendMarkRange(other)
      .insertContent({
        type: 'text',
        text,
        marks: [
          target.kind === 'page'
            ? { type: 'pageLink', attrs: { pageId: target.pageId } }
            : { type: 'link', attrs: { href: target.href } },
        ],
      })
      .run()
    setLinkDialogOpen(false)
  }

  const removeLink = () => {
    editor.chain().focus().extendMarkRange('link').unsetLink().extendMarkRange('pageLink').unsetPageLink().run()
    setLinkDialogOpen(false)
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
      ref={toolbarRef}
      onKeyDown={onKeyDown}
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

      <MarkToggle label="Bold" active={editor.isActive('bold')} onToggle={() => editor.chain().focus().toggleBold().run()}>
        <FormatBoldIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Italic"
        active={editor.isActive('italic')}
        onToggle={() => editor.chain().focus().toggleItalic().run()}
      >
        <FormatItalicIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Strikethrough"
        active={editor.isActive('strike')}
        onToggle={() => editor.chain().focus().toggleStrike().run()}
      >
        <StrikethroughSIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Inline code"
        active={editor.isActive('code')}
        onToggle={() => editor.chain().focus().toggleCode().run()}
      >
        <CodeIcon fontSize="small" />
      </MarkToggle>
      {/* Stateful too: a caret inside a link opens the dialog on that link, so
          "there is a link here" is worth showing the same way bold is. */}
      <MarkToggle
        label={currentLink ? 'Edit link' : 'Insert link'}
        active={currentLink !== null}
        onToggle={() => setLinkDialogOpen(true)}
      >
        <LinkIcon fontSize="small" />
      </MarkToggle>

      <Divider orientation="vertical" flexItem sx={{ mx: 0.5 }} />

      <MarkToggle
        label="Bulleted list"
        active={editor.isActive('bulletList')}
        onToggle={() => editor.chain().focus().toggleBulletList().run()}
      >
        <FormatListBulletedIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Numbered list"
        active={editor.isActive('orderedList')}
        onToggle={() => editor.chain().focus().toggleOrderedList().run()}
      >
        <FormatListNumberedIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Task list"
        active={editor.isActive('taskList')}
        onToggle={() => editor.chain().focus().toggleTaskList().run()}
      >
        <ChecklistIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Quote"
        active={editor.isActive('blockquote')}
        onToggle={() => editor.chain().focus().toggleBlockquote().run()}
      >
        <FormatQuoteIcon fontSize="small" />
      </MarkToggle>
      <MarkToggle
        label="Code block"
        active={editor.isActive('codeBlock')}
        onToggle={() => editor.chain().focus().toggleCodeBlock().run()}
      >
        <CodeBlockIcon fontSize="small" />
      </MarkToggle>

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

      {/*
        The contextual table controls sit AFTER every fixed control, never
        among them. They used to be inserted next to "Insert table", so putting
        the caret in a table pushed Callout, Diagram, Page list, Form, Emoji,
        GitLab and Horizontal rule ~200px to the right — and on a wrapping
        toolbar, sometimes onto another row. The user's pointer is over the
        editing surface when that happens and the button they were reaching for
        has moved. At the end, appearing costs nothing that was already there.
      */}
      {tableState && (
        <>
          <Divider orientation="vertical" flexItem sx={{ mx: 0.5 }} />
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
        </>
      )}

      {/* Dialogs live outside the toolbar's control flow — they render into a
          portal, so they are never part of the roving tab ring above. */}
      <InsertLinkDialog
        open={linkDialogOpen}
        initialText={linkDialogText()}
        existing={currentLink}
        onClose={() => setLinkDialogOpen(false)}
        onSubmit={applyLink}
        onRemove={currentLink ? removeLink : undefined}
      />
      <InsertPageListDialog
        open={pageListDialogOpen}
        onClose={() => setPageListDialogOpen(false)}
        onInsert={insertPageList}
      />
      <InsertFormDialog open={formDialogOpen} onClose={() => setFormDialogOpen(false)} onInsert={insertForm} />
      {gitlabConfigured && (
        <>
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
    </Box>
  )
}
