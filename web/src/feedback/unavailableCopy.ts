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
export function describeAskUnavailable(
  reason: AskWikiUnavailableReason | 'REQUEST_FAILED',
  /**
   * `assistantStatus.maxQuestionChars`, when it is known. Optional and
   * nullable on purpose: null is what an unconfigured assistant reports and
   * what a failed status query leaves behind, and in both cases the copy must
   * fall back to naming no number at all rather than to a default this client
   * invented. Only QUESTION_TOO_LONG reads it.
   */
  maxQuestionChars?: number | null,
): UnavailableCopy {
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
    case 'QUESTION_TOO_LONG': {
      // Says REFUSED, not trimmed, and that is the whole point of the copy.
      // The server rejects an over-long question outright rather than
      // truncating it, so that the person who wrote it decides what to cut
      // instead of silently getting an answer to some prefix of what they
      // asked. A generic "something went wrong" would throw that away.
      const refused =
        "That question is too long for this wiki, so it wasn't sent to the assistant and nothing was answered. It is refused rather than shortened for you, so that what gets cut is your choice."
      // The limit, only when the instance has actually reported one. Before
      // `assistantStatus` existed this sentence could not be written at all:
      // `Ai:MaxQuestionChars` is per-instance configuration, and a figure
      // guessed from the default would tell someone on a lower-limit instance
      // to cut to a length that would be refused again.
      if (typeof maxQuestionChars !== 'number' || maxQuestionChars <= 0) {
        return { summary: refused, pointsToSettings: false }
      }
      return {
        summary: `${refused} This wiki accepts up to ${maxQuestionChars.toLocaleString()} characters.`,
        pointsToSettings: false,
      }
    }
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
  /** The page-property key registry (design.md §20.1) — instance vocabulary, so it can name itself. */
  | 'PROPERTY_KEY_REGISTRY'
  /**
   * The registered `nationality` attribute behind the eyes-only caveat
   * (design.md §21.4). Kept apart from PROPERTY_KEY_REGISTRY because they are
   * different registries and a user told "the property key registry is
   * unreachable" while editing a marking would go looking in the wrong place.
   */
  | 'NATIONALITY_VOCABULARY'
  /**
   * The accumulated group vocabulary (design.md §6.6). Names the instance has
   * observed in tokens, never a membership list — so, like the other
   * registries, it has nothing to conceal and can say what failed.
   */
  /**
   * The three homepage feeds, each named separately so a section can say what
   * IT could not load while the other two carry on. One shared 'feed' reason
   * would make a reader wonder which of the three the message was about.
   */
  | 'ACTIVITY_FEED'
  | 'STALE_CONTENT'
  | 'RECENTLY_VIEWED'
  | 'GROUP_LIST'
  /**
   * The account roster (instance admins only). Identity and activity, with no
   * per-row permission filter — so, like the audit log, it can name what
   * failed rather than staying blurry.
   */
  | 'USER_ROSTER'
  | 'SYNC_STATUS'
  /**
   * The usage report (design.md §7). Distinct from the null the server returns
   * to a caller who administers neither the instance nor the space: that is an
   * answer, this is the absence of one, and the screen said the same sentence
   * for both — telling an admin whose API was down that they lacked permission.
   */
  | 'ANALYTICS'
  /** The notification inbox — a failed read must not read as an empty one. */
  | 'NOTIFICATIONS'
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
    case 'PROPERTY_KEY_REGISTRY':
      return { summary: "Couldn't load the property key registry.", pointsToSettings: false }
    case 'NATIONALITY_VOCABULARY':
      return {
        summary: "Couldn't load the countries an eyes-only caveat can name. A classification on its own still works.",
        pointsToSettings: false,
      }
    case 'NOTIFICATIONS':
      return { summary: "Couldn't load notifications.", pointsToSettings: false }
    case 'ACTIVITY_FEED':
      return { summary: "Couldn't load recent activity.", pointsToSettings: false }
    case 'STALE_CONTENT':
      return { summary: "Couldn't load your stale pages.", pointsToSettings: false }
    case 'RECENTLY_VIEWED':
      return { summary: "Couldn't load what you viewed recently.", pointsToSettings: false }
    case 'GROUP_LIST':
      return { summary: "Couldn't load the group list.", pointsToSettings: false }
    case 'USER_ROSTER':
      return { summary: "Couldn't load the user list. It is available to instance admins.", pointsToSettings: false }
    case 'SYNC_STATUS':
      return { summary: "Couldn't load sync status.", pointsToSettings: false }
    case 'ANALYTICS':
      return { summary: "Couldn't load the usage report.", pointsToSettings: false }
    case 'SEARCH':
      return { summary: "Couldn't search — there's no live API in this environment yet.", pointsToSettings: false }
  }
}

/**
 * Writes whose response never arrived — the request failed at the transport,
 * so the client genuinely does not know whether the server acted. Kept apart
 * from `LoadFailureReason` because that describes *reads*, and apart from the
 * typed mutation errors (graphql/mutationError.ts) because those are the
 * server's considered refusals with their own designed UX. All of these say
 * what didn't happen, since "try again" is the only useful next step.
 */
export type WriteFailureReason =
  | 'PAGE_PROPERTY'
  | 'PROPERTY_KEY'
  | 'PAGE_MARKING'
  /** Creating a space, and restoring a trashed batch — each its screen's primary action. */
  | 'SPACE'
  | 'RESTORE_PAGE'
  | 'RESTORE_SPACE'
  /** Watching/unwatching a page or space. */
  | 'WATCH'
  /** Submitting a record through a `form-definition` fence. */
  | 'FORM_ENTRY'
  /** Reassigning who is accountable for a space (design.md §6.5) — not an access change. */
  | 'SPACE_OWNER'

export function describeWriteFailure(reason: WriteFailureReason): UnavailableCopy {
  switch (reason) {
    case 'SPACE':
      return { summary: "Couldn't reach the API — the space wasn't created.", pointsToSettings: false }
    case 'RESTORE_SPACE':
      return { summary: "Couldn't reach the API — the space wasn't restored.", pointsToSettings: false }
    case 'RESTORE_PAGE':
      return { summary: "Couldn't reach the API — nothing was restored.", pointsToSettings: false }
    case 'WATCH':
      return { summary: "Couldn't reach the API — your watch setting is unchanged.", pointsToSettings: false }
    case 'FORM_ENTRY':
      return { summary: "Couldn't reach the API — that record wasn't saved.", pointsToSettings: false }
    case 'SPACE_OWNER':
      // Names who still owns it rather than only what failed: the question
      // behind the retry is 'so who is accountable right now'.
      return { summary: "Couldn't reach the API — this space's owner is unchanged.", pointsToSettings: false }
    case 'PAGE_PROPERTY':
      return { summary: "Couldn't reach the API — that property change wasn't saved.", pointsToSettings: false }
    case 'PROPERTY_KEY':
      return { summary: "Couldn't reach the API — that registry change wasn't saved.", pointsToSettings: false }
    case 'PAGE_MARKING':
      // Names what the page still carries rather than only what failed: a
      // marking is an access control (design.md §21), so "which one is in
      // force right now" is the fact the user is actually asking about.
      return {
        summary: "Couldn't reach the API — this page's marking is unchanged.",
        pointsToSettings: false,
      }
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
 * The `page-list` widget (design.md §22). Kept apart from `LoadFailureReason`
 * because the interesting failure here is not a load at all: an RQL query
 * that cannot run is AUTHORED CONTENT that came back refused, so the widget
 * is reporting on the page's own Markdown rather than on the API.
 *
 * Neither reason may hint at pages the reader cannot see. §6.7 makes an
 * invisible page indistinguishable from a nonexistent one, and §22.4 extends
 * that to the query itself — an invisible space compiles to the same
 * never-matches predicate an imaginary one does. A message that said "some
 * results may be hidden" would undo both.
 */
export type PageListUnavailableReason = 'REQUEST_FAILED' | 'QUERY_INVALID'

export function describePageListUnavailable(reason: PageListUnavailableReason): UnavailableCopy {
  switch (reason) {
    case 'REQUEST_FAILED':
      return { summary: "Couldn't reach the API to run this page list.", pointsToSettings: false }
    case 'QUERY_INVALID':
      // Names the query, not the reader: the fix is an edit to this page, and
      // the positioned errors that follow this sentence say what to edit.
      return { summary: "This list's query can't run as written, so no pages are listed.", pointsToSettings: false }
  }
}

/**
 * The instance with no spaces in it yet.
 *
 * Two surfaces say this — the space list and the rail's tree — and they had
 * drifted: the list branched on whether the reader could actually create a
 * space, and the rail told everyone "create one to start writing" while hiding
 * the button that would (design.md §6.5.1 makes creation instance-admin-only).
 * Telling a reader to do something they cannot is worse than telling them
 * nothing, so the branch lives here and both surfaces read it.
 *
 * Follows web/README.md's empty-state rule: the fact AND the consequence.
 */
export function describeNoSpaces(isInstanceAdmin: boolean): string {
  return isInstanceAdmin
    ? 'No spaces yet — create one to start writing.'
    : 'No spaces yet — an instance admin can create the first one.'
}

/**
 * A space with no pages in it yet. The space browser rendered an empty list and
 * nothing else, which is the first thing anyone sees after making a space.
 */
export function describeNoPages(canCreate: boolean): string {
  return canCreate
    ? 'No pages yet — use New page to write the first one.'
    : 'No pages yet in this space.'
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
  'Content arrives via one-way sync; editing happens on the origin instance.'
