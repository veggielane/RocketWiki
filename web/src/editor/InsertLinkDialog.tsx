import { useState } from 'react'
import {
  Autocomplete,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
} from '@mui/material'
import { usePageSpaceRefQuery, useSpacePageTreeQuery } from '../graphql/generated/graphql'
import { PAGE_TREE_CONTEXT } from '../graphql/treeDependencies'
import { useCurrentPageId } from '../pages/pageContext'
import { flattenParentOptions } from '../pages/parentOptions'
import { readableTree } from '../pages/treeEntries'
import { useDialogFullScreen } from '../app/useDialogFullScreen'

/**
 * The two link targets design.md §4 gives the editor: an ordinary external URL
 * (`[text](https://…)`, StarterKit's `link` mark) and a wiki page
 * (`[title](page://{id})`, the `pageLink` mark — stable across renames).
 *
 * Deliberately NOT `attachment://` or `user://`, though both are in §4's table:
 * they are not links. `attachment://` is an image src (`![alt](attachment://{id})`,
 * inserted by the attachment button and by drag/paste) and `user://` is an
 * atomic mention node (`@[display](user://{id})`). Offering either here would
 * produce Markdown the round trip does not accept.
 */
export type LinkTarget =
  | { kind: 'external'; href: string }
  | { kind: 'page'; pageId: string }

export interface InsertLinkDialogProps {
  open: boolean
  /** Pre-filled from the selection, so linking selected text does not mean retyping it. */
  initialText: string
  /** The link under the cursor, when there is one — makes this an edit rather than an insert. */
  existing: LinkTarget | null
  onClose: () => void
  onSubmit: (target: LinkTarget, text: string) => void
  /** Only offered when the cursor is actually inside a link. */
  onRemove?: () => void
}

/** Rejects the schemes a link mark must never carry — `javascript:` above all. */
function isUsableHref(href: string): boolean {
  const trimmed = href.trim()
  if (trimmed.length === 0) return false
  try {
    const { protocol } = new URL(trimmed)
    return protocol === 'http:' || protocol === 'https:'
  } catch {
    return false
  }
}

/**
 * Replaces the `window.prompt('Link URL (https://…)')` that used to be the only
 * way to make a link — the single most-used editor action, and the only
 * affordance in the app that was not a real dialog. The prompt could not reach
 * `page://` at all (so §4's rename-stable internal link was unreachable from the
 * UI), could not edit or remove an existing link, validated nothing, and is
 * blocked outright in cross-origin iframes.
 */
export function InsertLinkDialog({
  open,
  initialText,
  existing,
  onClose,
  onSubmit,
  onRemove,
}: InsertLinkDialogProps) {
  const fullScreen = useDialogFullScreen()
  // Seeded from the props at mount as well as re-seeded on open below: the
  // toolbar keeps this mounted and toggles `open`, but a caller that mounts it
  // already open (and every test does) would otherwise get empty fields.
  const [kind, setKind] = useState<LinkTarget['kind']>(existing?.kind ?? 'external')
  const [href, setHref] = useState(existing?.kind === 'external' ? existing.href : '')
  const [pageId, setPageId] = useState<string | null>(existing?.kind === 'page' ? existing.pageId : null)
  const [text, setText] = useState(initialText)

  // Re-seeded each time the dialog opens rather than on mount: the toolbar keeps
  // it mounted, so a stale draft from the last link would otherwise be sitting
  // there when the next one opens. Adjusted during render against a tracked
  // copy — the repo's "adjust state when an input changes" idiom, as
  // CreatePageDialog does for its parent default — rather than in an effect,
  // which would paint the previous link's values for one frame.
  const [wasOpen, setWasOpen] = useState(open)
  if (wasOpen !== open) {
    setWasOpen(open)
    if (open) {
      setText(initialText)
      setKind(existing?.kind ?? 'external')
      setHref(existing?.kind === 'external' ? existing.href : '')
      setPageId(existing?.kind === 'page' ? existing.pageId : null)
    }
  }

  // The pages this link can point at: the current page's own space, flattened
  // and indented, from the same tree query the rail already has cached. A
  // cross-space picker would need a search endpoint and a permission story of
  // its own; same-space covers the linking people actually do.
  const currentPageId = useCurrentPageId()
  const [{ data: spaceRef }] = usePageSpaceRefQuery({
    variables: { id: currentPageId ?? '' },
    pause: !currentPageId || !open,
  })
  const spaceId = spaceRef?.page?.spaceId
  const [{ data: treeData }] = useSpacePageTreeQuery({
    variables: { spaceId: spaceId ?? '' },
    pause: !spaceId || !open,
    context: PAGE_TREE_CONTEXT,
  })
  // `flattenParentOptions` leads with "(top level)", which is a parent choice
  // and not a page — a link cannot point at it. Protected entries are dropped
  // first: a page this author cannot read has no id to link to, and offering
  // its placeholder would be offering nothing.
  const pageOptions = flattenParentOptions(readableTree(treeData?.pageTree ?? [])).filter((option) => option.id !== null)

  const targetValid = kind === 'external' ? isUsableHref(href) : pageId !== null
  const canSubmit = targetValid && text.trim().length > 0

  const submit = () => {
    if (!canSubmit) return
    onSubmit(kind === 'external' ? { kind: 'external', href: href.trim() } : { kind: 'page', pageId: pageId! }, text)
  }

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm" fullScreen={fullScreen}>
      <DialogTitle>{existing ? 'Edit link' : 'Insert link'}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={kind}
            onChange={(_e, next: LinkTarget['kind'] | null) => next && setKind(next)}
            aria-label="Link target"
          >
            <ToggleButton value="external">Web address</ToggleButton>
            <ToggleButton value="page">Wiki page</ToggleButton>
          </ToggleButtonGroup>

          <TextField
            label="Text"
            value={text}
            onChange={(e) => setText(e.target.value)}
            fullWidth
            size="small"
            autoFocus
            helperText="What the link reads as in the page."
          />

          {kind === 'external' ? (
            <TextField
              label="Web address"
              value={href}
              onChange={(e) => setHref(e.target.value)}
              fullWidth
              size="small"
              // Only flagged once there is something to be wrong about — an
              // empty field on a freshly opened dialog is not an error.
              error={href.trim().length > 0 && !isUsableHref(href)}
              helperText={
                href.trim().length > 0 && !isUsableHref(href)
                  ? 'Needs a full http:// or https:// address.'
                  : 'For example https://example.com/runbook.'
              }
            />
          ) : (
            <Autocomplete
              size="small"
              options={pageOptions}
              getOptionLabel={(option) => option.title}
              isOptionEqualToValue={(option, value) => option.id === value.id}
              value={pageOptions.find((option) => option.id === pageId) ?? null}
              onChange={(_e, option) => setPageId(option?.id ?? null)}
              renderInput={(params) => (
                <TextField
                  {...params}
                  label="Page"
                  helperText="Links by page id, so it survives the page being renamed or moved."
                />
              )}
            />
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        {onRemove && (
          <Button
            color="error"
            onClick={onRemove}
            // Pushed away from the confirming pair: it is the one action here
            // that undoes rather than applies.
            sx={{ mr: 'auto' }}
          >
            Remove link
          </Button>
        )}
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" disabled={!canSubmit} onClick={submit}>
          {existing ? 'Update' : 'Insert'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
