import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Paper, Stack, Typography } from '@mui/material'
import AccountCircleOutlinedIcon from '@mui/icons-material/AccountCircleOutlined'
import { AVATAR_ACCEPTED_TYPES, AvatarUploadFailure, clearAvatar, uploadAvatar } from './avatarApi'
import { invalidateAvatar } from './avatarCache'
import { UserAvatar } from './UserAvatar'
import { formatBytes } from '../attachments/formatBytes'

export interface AvatarSettingsSectionProps {
  /** `me.localUserId` — needed to show the current avatar and evict it from the cache after a change. */
  localUserId: string | null | undefined
  hasAvatar: boolean
  displayName: string
  /** Called after a successful upload/clear so the caller refetches `me` (the `hasAvatar` flag is server truth). */
  onChanged: () => void
}

/**
 * The profile-picture surface on Settings (design.md §19). Deliberately a
 * *preview-and-upload*, not a client-side cropper: the server is the
 * normalizer (center-crop, resize to canonical 512, re-encode to PNG,
 * metadata stripped — the client's bytes are never stored as-is), so a
 * canvas cropper would add code without adding fidelity. The circular
 * preview uses CSS `cover` + center, which shows exactly what the server's
 * center-crop will keep; the original file is what gets uploaded.
 */
export function AvatarSettingsSection({ localUserId, hasAvatar, displayName, onChanged }: AvatarSettingsSectionProps) {
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [pending, setPending] = useState<{ file: File; previewUrl: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [feedback, setFeedback] = useState<{ severity: 'success' | 'warning'; message: string } | null>(null)

  // The preview object URL is component-owned (unlike the shared cache's
  // URLs) — revoked when replaced (in `choose`/upload/cancel) and on
  // unmount. The ref mirror (kept in an effect, never during render)
  // exists because an unmount-only cleanup would otherwise close over the
  // first render's `pending` (always null).
  const pendingRef = useRef(pending)
  useEffect(() => {
    pendingRef.current = pending
  }, [pending])
  useEffect(() => {
    return () => {
      if (pendingRef.current) URL.revokeObjectURL(pendingRef.current.previewUrl)
    }
  }, [])

  const choose = (file: File | undefined) => {
    setFeedback(null)
    if (!file) return
    if (!AVATAR_ACCEPTED_TYPES.includes(file.type)) {
      setFeedback({ severity: 'warning', message: 'Avatars can be PNG, JPEG, or WebP images.' })
      return
    }
    if (pending) URL.revokeObjectURL(pending.previewUrl)
    setPending({ file, previewUrl: URL.createObjectURL(file) })
  }

  const describeFailure = (error: unknown): string => {
    if (error instanceof AvatarUploadFailure) {
      if (error.detail.kind === 'tooLarge') {
        const cap = error.detail.maxSizeBytes
        return cap !== null
          ? `That image is too big — avatars can be up to ${formatBytes(cap)}.`
          : 'That image is too big for an avatar.'
      }
      if (error.detail.message) return error.detail.message
    }
    return "Couldn't update the profile picture."
  }

  const handleUpload = async () => {
    if (!pending) return
    setBusy(true)
    setFeedback(null)
    try {
      await uploadAvatar(pending.file)
      URL.revokeObjectURL(pending.previewUrl)
      setPending(null)
      if (localUserId) invalidateAvatar(localUserId)
      setFeedback({ severity: 'success', message: 'Profile picture updated.' })
      onChanged()
    } catch (error) {
      setFeedback({ severity: 'warning', message: describeFailure(error) })
    } finally {
      setBusy(false)
    }
  }

  const handleClear = async () => {
    setBusy(true)
    setFeedback(null)
    try {
      await clearAvatar()
      if (localUserId) invalidateAvatar(localUserId)
      setFeedback({ severity: 'success', message: 'Profile picture removed.' })
      onChanged()
    } catch (error) {
      setFeedback({ severity: 'warning', message: describeFailure(error) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Paper variant="outlined" sx={{ p: 3, maxWidth: 640 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
        <AccountCircleOutlinedIcon fontSize="small" />
        <Typography variant="h6" component="h2">
          Profile picture
        </Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Shown beside your comments and presence. The image is center-cropped to a square and resized on the
        server; camera metadata never leaves it. PNG, JPEG, or WebP.
      </Typography>

      {feedback && (
        <Alert severity={feedback.severity} sx={{ mb: 2 }} onClose={() => setFeedback(null)}>
          {feedback.message}
        </Alert>
      )}

      <Stack direction="row" spacing={3} sx={{ alignItems: 'center' }}>
        {pending ? (
          // Center-crop preview: CSS cover/center shows exactly the square
          // the server's own center-crop will keep.
          <Box
            role="img"
            aria-label="New profile picture preview"
            sx={{
              width: 64,
              height: 64,
              borderRadius: '50%',
              backgroundImage: `url(${pending.previewUrl})`,
              backgroundSize: 'cover',
              backgroundPosition: 'center',
              flexShrink: 0,
            }}
          />
        ) : (
          <UserAvatar userId={localUserId} hasAvatar={hasAvatar} displayName={displayName} size={64} />
        )}

        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
          <input
            ref={fileInputRef}
            type="file"
            accept={AVATAR_ACCEPTED_TYPES.join(',')}
            hidden
            aria-label="Choose profile picture"
            onChange={(e) => {
              choose(e.target.files?.[0])
              e.target.value = '' // re-selecting the same file re-fires change
            }}
          />
          <Button variant="outlined" size="small" onClick={() => fileInputRef.current?.click()} disabled={busy}>
            Choose image
          </Button>
          {pending && (
            <>
              <Button variant="contained" size="small" onClick={() => void handleUpload()} disabled={busy}>
                Upload
              </Button>
              <Button
                size="small"
                disabled={busy}
                onClick={() => {
                  URL.revokeObjectURL(pending.previewUrl)
                  setPending(null)
                }}
              >
                Cancel
              </Button>
            </>
          )}
          {!pending && hasAvatar && (
            <Button color="error" size="small" onClick={() => void handleClear()} disabled={busy}>
              Remove picture
            </Button>
          )}
        </Stack>
      </Stack>
    </Paper>
  )
}
