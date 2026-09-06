import { matchPath } from 'react-router-dom'

/**
 * The reading measure for everything in the content region. The template's
 * dashboard runs nearly full-bleed, which suits a grid of cards and not a wiki:
 * prose at 1150px is a 150-character line. The header strip shares the value so
 * the two read as one column rather than as a full-width bar over a narrow one.
 */
export const CONTENT_MAX_WIDTH = 960

/**
 * The measure for screens that are tables and charts rather than prose. The
 * audit log's columns alone sum past 1100px, so at 960 it horizontally scrolled
 * at every viewport size — a scan-across-columns screen inside a measure
 * designed for a 90-character line.
 *
 * The registries stay at the prose measure deliberately: three narrow columns
 * spread over 1400px is not a better table, and the emoji grid caps itself at
 * 640 regardless.
 */
export const WIDE_MAX_WIDTH = 1400

/**
 * Matched as ROUTE PATTERNS, not path prefixes. Prefix matching made the
 * measure a property of the ADDRESS, and `AnalyticsPage` is mounted at two of
 * them: the site-wide `/admin/analytics` got 1400 while the identical
 * space-scoped `/spaces/:spaceKey/-/analytics` got 960, so the same component
 * laid its charts out differently depending on which link you followed.
 */
// `/graph` is wide for the same reason analytics is: a force-directed drawing
// and a five-column table of links are things to scan across, not prose.
const WIDE_ROUTE_PATTERNS = ['/admin/audit', '/admin/analytics', '/admin/sync', '/spaces/:spaceKey/-/analytics', '/graph']

/**
 * How wide the content column is on the screen at `pathname`.
 *
 * Its own module rather than a second export from AppShell: a file that exports
 * both a component and a plain function loses React Fast Refresh for the
 * component, and this is exactly the kind of pure decision a test should be able
 * to ask about directly.
 */
export function measureFor(pathname: string): number {
  const wide = WIDE_ROUTE_PATTERNS.some((path) => matchPath({ path, end: false }, pathname) !== null)
  return wide ? WIDE_MAX_WIDTH : CONTENT_MAX_WIDTH
}
