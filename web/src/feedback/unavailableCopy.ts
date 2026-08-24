import type { AskWikiUnavailableReason, GitLabUnavailableReason } from '../graphql/generated/graphql'

/**
 * The app-wide degradation vocabulary: every "this can't work right now"
 * surface translates its reasons here, once, so the copy can't drift
 * between surfaces. Grown out of the GitLab §18 module that set the voice:
 * say what is unavailable, say why in one sentence, and point at the fix
 * only when the user can actually apply it.
 *
 * Each surface keeps its OWN reason set — a missing GitLab token, an
 * unreachable model endpoint, and an undecodable diagram payload are
 * different facts and must stay distinguishable; what is shared is the
 * shape and the voice, not the semantics.
 */
export interface UnavailableCopy {
  summary: string
  /** True for the reasons the user can fix themselves on the Settings page. */
  pointsToSettings: boolean
}

/**
 * GitLab surfaces (design.md §18) — chip tooltips, file-embed placeholders,
 * issue-list placeholders. The two credential reasons point at Settings
 * because that page *is* the fix; NOT_FOUND deliberately stays blurry
 * (GitLab collapses 403/404 for unauthorized resources and we must not
 * sharpen a distinction upstream chose to blur).
 */
export function describeGitLabUnavailable(reason: GitLabUnavailableReason): UnavailableCopy {
  switch (reason) {
    case 'NOT_CONFIGURED':
      return { summary: 'GitLab integration is not configured on this instance.', pointsToSettings: false }
    case 'NO_CREDENTIAL':
      return {
        summary: 'No GitLab token saved for your account — add one in Settings to see live GitLab content.',
        pointsToSettings: true,
      }
    case 'INVALID_CREDENTIAL':
      return {
        summary: 'GitLab rejected your token — update it in Settings.',
        pointsToSettings: true,
      }
    case 'NOT_FOUND':
      return { summary: 'GitLab has no such item visible to you.', pointsToSettings: false }
    case 'UNREACHABLE':
      return { summary: 'GitLab could not be reached.', pointsToSettings: false }
  }
}

/**
 * Ask the wiki (design.md §9.5). The schema's reasons plus REQUEST_FAILED,
 * the transport-level "couldn't even reach our own API" state — a designed
 * retryable outcome on this surface, never a raw toast. None point at
 * Settings: the assistant endpoint is instance configuration, not a
 * per-user credential.
 */
export function describeAskUnavailable(reason: AskWikiUnavailableReason | 'REQUEST_FAILED'): UnavailableCopy {
  switch (reason) {
    case 'NO_RESULTS':
      return { summary: 'Nothing in the wiki you can view answers this.', pointsToSettings: false }
    case 'UNREACHABLE':
      return {
        summary: "The assistant endpoint couldn't be reached, so this question wasn't answered. The wiki itself is fine.",
        pointsToSettings: false,
      }
    case 'NOT_CONFIGURED':
      return { summary: "The wiki assistant isn't configured on this instance.", pointsToSettings: false }
    case 'REQUEST_FAILED':
      return { summary: "Couldn't reach the wiki API to ask this. Check your connection and retry.", pointsToSettings: false }
  }
}

/**
 * Attachments (design.md §10). §6.7 applies to the read reasons: absent and
 * not-viewable-to-you are deliberately indistinguishable, so neither
 * message may sharpen the distinction. INLINE_IMAGE is terse by design —
 * it renders as a placeholder inside flowing page text, not as an alert.
 *
 * UPLOAD_FAILED covers both ways a file enters a page — the attachment
 * button and a drag/paste into the editor — because a user who drops an
 * image and a user who picks one are reporting the same failure and must
 * not have to learn two vocabularies for it.
 */
export type AttachmentUnavailableReason =
  | 'INLINE_IMAGE'
  | { kind: 'DOWNLOAD_FAILED'; fileName: string }
  | { kind: 'UPLOAD_FAILED'; fileName: string }

export function describeAttachmentUnavailable(reason: AttachmentUnavailableReason): UnavailableCopy {
  if (reason === 'INLINE_IMAGE') {
    return { summary: 'Image unavailable', pointsToSettings: false }
  }
  if (reason.kind === 'UPLOAD_FAILED') {
    return {
      summary: `Couldn't upload "${reason.fileName}" — there's no live API in this environment yet.`,
      pointsToSettings: false,
    }
  }
  return { summary: `Couldn't download "${reason.fileName}".`, pointsToSettings: false }
}

/**
 * Transport degradation: a query errored, or came back with nothing where
 * the surface needs something. Every one of these was written ad hoc at its
 * call site during the feature waves, which is how "No spaces loaded." —
 * an EMPTY-state sentence for a FAILURE — ended up on two surfaces; the
 * reasons are collected here so the shape stays one shape.
 *
 * The reasons stay distinct rather than collapsing into one "couldn't load"
 * because they are different facts: SPACE/PAGE/TRASH must stay blurry about
 * absent-vs-not-yours (design.md §6.7 — a sharper message would confirm a
 * page exists to someone with no right to know), while an audit query or the
 * emoji registry has nothing to conceal and can name what failed.
 */
export type LoadFailureReason =
  | 'SPACE_LIST'
  | 'SPACE'
  | 'PAGE'
  | 'TRASH'
  | 'ARCHIVED_SPACES'
  | 'AUDIT_EVENTS'
  | 'EMOJI_REGISTRY'
  | 'SYNC_STATUS'
  | 'SEARCH'
  /** The what-if inspector: `forPrincipal` distinguishes "your own view" from a staged subject. */
  | { kind: 'PERMISSION_INSPECTION'; forPrincipal: boolean }

export function describeLoadFailure(reason: LoadFailureReason): UnavailableCopy {
  if (typeof reason === 'object') {
    return {
      summary: `Couldn't inspect permissions${reason.forPrincipal ? ' for that principal' : ''} on this page.`,
      pointsToSettings: false,
    }
  }
  switch (reason) {
    case 'SPACE_LIST':
      return { summary: "Couldn't load the space list.", pointsToSettings: false }
    case 'SPACE':
      return { summary: "Couldn't load this space.", pointsToSettings: false }
    case 'PAGE':
      return { summary: "Couldn't load this page.", pointsToSettings: false }
    case 'TRASH':
      return { summary: "Couldn't load trash — the space may not exist, or the API isn't reachable.", pointsToSettings: false }
    case 'ARCHIVED_SPACES':
      return { summary: "Couldn't load archived spaces.", pointsToSettings: false }
    case 'AUDIT_EVENTS':
      return { summary: "Couldn't load audit events.", pointsToSettings: false }
    case 'EMOJI_REGISTRY':
      return { summary: "Couldn't load the emoji registry.", pointsToSettings: false }
    case 'SYNC_STATUS':
      return { summary: "Couldn't load sync status.", pointsToSettings: false }
    case 'SEARCH':
      return { summary: "Couldn't search — there's no live API in this environment yet.", pointsToSettings: false }
  }
}

/**
 * Diagram render failures. Both reasons mean "the stored source is intact,
 * only the picture is missing" — the caller appends the mechanical detail
 * (parser message / decode reason) after the summary, and the drawio
 * surface adds its payload-retention reassurance.
 */
export type DiagramUnavailableReason = 'MERMAID_SOURCE' | 'DRAWIO_PAYLOAD'

export function describeDiagramUnavailable(reason: DiagramUnavailableReason): UnavailableCopy {
  switch (reason) {
    case 'MERMAID_SOURCE':
      return { summary: "Diagram doesn't render.", pointsToSettings: false }
    case 'DRAWIO_PAYLOAD':
      return { summary: "Diagram can't be displayed.", pointsToSettings: false }
  }
}

/**
 * Replica spaces (design.md §12 — and §12's word IS "replica", so no
 * user-facing surface says "mirror"/"mirrored"). The badge form is shared
 * by the space-list chip, the tree secondary line, and the lead of every
 * banner; the explanation sentence rides behind it wherever there is room
 * for a why.
 */
export function replicaBadgeLabel(originInstanceId: string | null | undefined): string {
  return `Replica of ${originInstanceId ?? 'another instance'} — read-only`
}

export const REPLICA_EXPLANATION =
  'Content arrives via one-way sync; editing happens on the origin instance (design.md §12).'
