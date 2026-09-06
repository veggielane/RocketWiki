import { useRef, useState } from 'react'
import { Alert, Button } from '@mui/material'
import UploadFileOutlinedIcon from '@mui/icons-material/UploadFileOutlined'
import { uploadAttachment, type UploadedAttachment } from './attachmentApi'
import { describeAttachmentUnavailable } from '../feedback/unavailableCopy'
import { PRINT_HIDDEN } from '../theme/print'

export function AttachmentUploadButton({
  pageId,
  onUploaded,
}: {
  pageId: string
  onUploaded: (attachment: UploadedAttachment) => void
}) {
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  const handleChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return
    setUploading(true)
    setError(null)
    try {
      const attachment = await uploadAttachment(pageId, file)
      onUploaded(attachment)
    } catch {
      setError(describeAttachmentUnavailable({ kind: 'UPLOAD_FAILED', fileName: file.name }).summary)
    } finally {
      setUploading(false)
      if (inputRef.current) inputRef.current.value = ''
    }
  }

  return (
    <>
      <Button component="label" size="small" startIcon={<UploadFileOutlinedIcon />} disabled={uploading} sx={PRINT_HIDDEN}>
        {uploading ? 'Uploading…' : 'Upload attachment'}
        <input ref={inputRef} type="file" hidden onChange={(e) => void handleChange(e)} />
      </Button>
      {error && (
        <Alert severity="error" sx={{ mt: 1 }}>
          {error}
        </Alert>
      )}
    </>
  )
}
