import type { GitLabUnavailableReason } from '../graphql/generated/graphql'

/**
 * The degradation vocabulary (design.md §18), translated once for every
 * GitLab surface — chip tooltips, file-embed placeholders, issue-list
 * placeholders — so the copy can't drift between them. The two credential
 * reasons point at Settings because that page *is* the fix; NOT_FOUND
 * deliberately stays blurry (GitLab collapses 403/404 for unauthorized
 * resources and we must not sharpen a distinction upstream chose to blur).
 */
export interface UnavailableCopy {
  summary: string
  /** True for the reasons the user can fix themselves on the Settings page. */
  pointsToSettings: boolean
}

export function describeUnavailable(reason: GitLabUnavailableReason): UnavailableCopy {
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
