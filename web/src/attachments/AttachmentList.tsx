import { useState } from 'react'
import { Alert, IconButton, List, ListItem, ListItemIcon, ListItemText, Stack, Tooltip } from '@mui/material'
import InsertDriveFileOutlinedIcon from '@mui/icons-material/InsertDriveFileOutlined'
import DownloadOutlinedIcon from '@mui/icons-material/DownloadOutlined'
import { fetchAttachmentBlob } from './attachmentApi'
import { formatBytes } from './formatBytes'
import { UserAvatar } from '../avatars/UserAvatar'

export interface AttachmentSummary {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  /** Resolved from `Attachment.uploadedBy` (UserRef). */
  uploadedByDisplayName: string
  /** `UserRef.id` — the local user id the avatar route takes (design.md §19). */
  uploadedById?: string
  /** `UserRef.hasAvatar` — image when true, initials otherwise, never a probing GET. */
  uploadedByHasAvatar?: boolean
}

/**
 * Per-page attachment list (design.md §10). Downloads stream through the
 * authenticated API and get handed to the browser as a blob — same "no
 * presigned URLs" rule as the inline editor images
 * (attachmentApi.ts / useAttachmentBlobUrl.ts), just triggered on click
 * instead of on render.
 */
export function AttachmentList({ attachments }: { attachments: AttachmentSummary[] }) {
  const [error, setError] = useState<string | null>(null)

  if (attachments.length === 0) {
    return null
  }

  const handleDownload = async (attachment: AttachmentSummary) => {
    setError(null)
    try {
      const blob = await fetchAttachmentBlob(attachment.id)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = attachment.fileName
      link.click()
      URL.revokeObjectURL(url)
    } catch {
      // Same message regardless of whether it's missing or just not
      // viewable to this user — design.md §6.7 applies here too.
      setError(`Couldn't download "${attachment.fileName}".`)
    }
  }

  return (
    <>
      {error && (
        <Alert severity="error" sx={{ mb: 1 }}>
          {error}
        </Alert>
      )}
      <List dense disablePadding>
        {attachments.map((attachment) => (
          <ListItem
            key={attachment.id}
            secondaryAction={
              <Tooltip title="Download">
                <IconButton edge="end" onClick={() => void handleDownload(attachment)} aria-label={`Download ${attachment.fileName}`}>
                  <DownloadOutlinedIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            }
          >
            <ListItemIcon>
              <InsertDriveFileOutlinedIcon fontSize="small" />
            </ListItemIcon>
            <ListItemText
              primary={attachment.fileName}
              // The secondary line holds an Avatar (a div) — rendered as a
              // div, not the default <p>, to keep the markup valid.
              slotProps={{ secondary: { component: 'div' } }}
              secondary={
                <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                  <span>{`${formatBytes(attachment.sizeBytes)} · uploaded by`}</span>
                  <UserAvatar
                    userId={attachment.uploadedById}
                    hasAvatar={attachment.uploadedByHasAvatar ?? false}
                    displayName={attachment.uploadedByDisplayName}
                    size={16}
                  />
                  <span>{attachment.uploadedByDisplayName}</span>
                </Stack>
              }
            />
          </ListItem>
        ))}
      </List>
    </>
  )
}
