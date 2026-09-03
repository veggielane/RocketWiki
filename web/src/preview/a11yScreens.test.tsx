// The accessibility capture set — the browser a11y layer's input (see
// docs/ACCESSIBILITY.md and web/a11y/). A deliberately more comprehensive
// sibling of captureScreens.test.tsx: every major screen plus the portal
// surfaces (notification popover, move dialog, stale-revision dialog), each
// rendered in BOTH themes — dark-mode contrast is where audits usually
// bleed — with real components, real staged data, and the repo's real
// stylesheets embedded.
//
// Two jobs in one file, by design:
//  1. Always (every `npm test`): each LIGHT screen gets the shared jsdom axe
//     pass (test/axe.ts — WCAG 2.2 AA tags minus the documented jsdom
//     exclusions). This is the component layer's whole-page sweep, covering
//     pages that have no dedicated test file (search, trash, audit log...).
//     Dark variants render but skip the jsdom axe run: axe-core sees the
//     same DOM structure in both themes and everything theme-dependent
//     (color-contrast) is excluded in jsdom anyway — the browser layer
//     checks BOTH theme variants with contrast on.
//  2. With PREVIEW_OUT=<dir>: writes one self-contained HTML file per
//     screen+theme for web/a11y's Playwright + axe run (real Chromium, real
//     CSS, color-contrast and target-size enforced there).
//
// Staged data only — no backend. Avatars/emojis are inline SVG data URIs.
import { mkdirSync, writeFileSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { afterAll, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AppShell } from '../app/AppShell'
import { PageViewPage } from '../pages/PageViewPage'
import { PageEditPage } from '../pages/PageEditPage'
import { SearchPage } from '../pages/SearchPage'
import { SettingsPage } from '../pages/SettingsPage'
import { AskWikiPage } from '../pages/AskWikiPage'
import { AdminEmojisPage } from '../pages/AdminEmojisPage'
import { AdminPropertyKeysPage } from '../pages/AdminPropertyKeysPage'
import { PageDetailsPage } from '../pages/PageDetailsPage'
import { PageHistoryPage } from '../pages/PageHistoryPage'
import { PagePermissionsPage } from '../pages/PagePermissionsPage'
import { SpaceBrowserPage } from '../pages/SpaceBrowserPage'
import { AnalyticsPage } from '../pages/AnalyticsPage'
import { HelpPage } from '../pages/HelpPage'
import { FormDefinitionBlock, FormListBlock } from '../forms/FormBlocks'
import { PageIdContext } from '../pages/pageContext'
import { TrashPage } from '../pages/TrashPage'
import { AuditLogPage } from '../pages/AuditLogPage'
import { SpaceListPage } from '../pages/SpaceListPage'
import { AdminPage } from '../pages/AdminPage'
import { RichTextEditor } from '../editor/RichTextEditor'
import { MovePageDialog } from '../access/move/MovePageDialog'
import { StaleRevisionDialog } from '../diff/StaleRevisionDialog'
import { Box } from '@mui/material'
import { MarkingBanner } from '../markings/MarkingBanner'
import { MarkingLevelBadge } from '../markings/MarkingLevelBadge'
import { CLASSIFICATION_LADDER } from '../markings/clearance'
import { ColorModeProvider } from '../theme/ColorModeProvider'
import { createMockUrqlClient } from '../test/mockUrqlClient'
import { expectNoAxeViolations } from '../test/axe'
import { setEmojiRegistry } from '../emoji/registry'
import { group } from '../access/ruleTypes'
import type { FakePresenceTransport } from '../realtime/FakePresenceTransport'

window.matchMedia ??= ((query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
  addListener: () => {},
  removeListener: () => {},
  onchange: null,
  dispatchEvent: () => false,
})) as never

const face = (bg: string, letter: string) =>
  `data:image/svg+xml;utf8,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128"><rect width="128" height="128" rx="64" fill="${bg}"/><text x="64" y="86" font-family="Arial" font-size="64" fill="#fff" text-anchor="middle">${letter}</text></svg>`,
  )}`

const emojiImg = (bg: string, glyph: string) =>
  `data:image/svg+xml;utf8,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" rx="14" fill="${bg}"/><text x="32" y="46" font-size="40" text-anchor="middle">${glyph}</text></svg>`,
  )}`

vi.mock('react-oidc-context', () => ({
  useAuth: () => ({
    isAuthenticated: true,
    isLoading: false,
    user: { profile: { name: 'Chris', preferred_username: 'chris', email: 'chris@rocketwiki.dev' } },
    signinRedirect: () => Promise.resolve(),
    signoutRedirect: () => Promise.resolve(),
    removeUser: () => Promise.resolve(),
  }),
}))

// A handle onto the presence fake so the page-view screen can stage live
// viewers + pointers (the presence-label contrast surface).
const realtime = vi.hoisted(() => ({ presence: undefined as unknown }))
vi.mock('../realtime/transports', async () => {
  const { FakePresenceTransport } = await import('../realtime/FakePresenceTransport')
  const { FakeNotificationsTransport } = await import('../realtime/FakeNotificationsTransport')
  const presence = new FakePresenceTransport()
  realtime.presence = presence
  const notifications = new FakeNotificationsTransport()
  return {
    getDefaultPresenceTransport: () => presence,
    getDefaultCoEditTransport: () => presence,
    getDefaultNotificationsTransport: () => notifications,
    createPresenceTransport: () => presence,
    createNotificationsTransport: () => notifications,
  }
})

const faceFor = (userId: string) =>
  userId === 'user-ada' ? face('#7b1fa2', 'A') : userId === 'user-grace' ? face('#00695c', 'G') : face('#1565c0', 'C')
const emojiFor = (name: string) => (name === 'rocket' ? emojiImg('#263238', '🚀') : emojiImg('#f9a825', '🍌'))

vi.mock('../avatars/avatarCache', () => ({
  getAvatarUrl: (userId: string) => Promise.resolve(faceFor(userId)),
  peekAvatarUrl: (userId: string) => faceFor(userId),
  invalidateAvatar: () => {},
  resetAvatarCache: () => {},
}))

vi.mock('../emoji/emojiBlobCache', () => ({
  getEmojiUrl: (name: string) => Promise.resolve(emojiFor(name)),
  peekEmojiUrl: (name: string) => emojiFor(name),
  resetEmojiBlobCache: () => {},
}))

const pageContent = [
  '# Stage two ignition anomaly review :rocket:',
  '',
  'The 14 August static fire showed a **270 ms ignition delay** on the stage',
  'two vacuum engine. This page tracks the investigation — see `PT-201` for',
  'the inlet pressure channel.',
  '',
  // Three in-page links, one per resolution (design.md §21.8): a readable
  // target (a router link), a withheld one (inert, with the `(protected)`
  // marker) and a vanished one (inert, with the missing marker) — so the
  // browser tier judges the two marker styles for contrast in both themes.
  'Related: [the chill-in procedure](page://page-3), [export-controlled test data](page://page-4) and [an old note](page://page-9).',
  '',
  '## Findings so far',
  '',
  '- Turbopump inlet pressure sagged during chill-in :banana:',
  '- Igniter feed line showed a transient the telemetry filter smoothed over',
  '',
  '## Corrective actions',
  '',
  '- [x] Re-run chill-in with extended pre-press hold',
  '- [ ] Add an unfiltered channel for igniter feed pressure',
  '',
  ':::info',
  'Full telemetry lives on the test-stand share; ask in #prop-test before',
  'the Friday review.',
  ':::',
  '',
  '```python',
  'redline = 24.1  # bar',
  'if pt201 < redline:',
  '    hold_prepress()',
  '```',
  '',
  '| Sensor | Nominal | Observed |',
  '| --- | --- | --- |',
  '| PT-201 | 24.1 bar | 21.8 bar |',
  '| TT-118 | 90.4 K | 92.5 K |',
  '',
].join('\n')

const page = {
  id: 'page-1',
  spaceId: 'space-eng',
  spaceKey: 'PROP',
  title: 'Stage two ignition anomaly review',
  slug: 'stage-two-ignition-anomaly',
  // Staged set, so the page header's glyph and the edit screen's icon picker
  // both reach the browser layer's contrast check in both themes.
  icon: 'ROCKET',
  content: pageContent,
  currentRevisionNumber: 7,
  canEdit: true,
  canComment: true,
  canManageAccess: true,
  viewerIsWatching: true,
  labels: ['anomaly', 'propulsion'],
  labelDetails: [
    { id: 'l-1', spaceId: 'space-eng', name: 'anomaly' },
    { id: 'l-2', spaceId: 'space-eng', name: 'propulsion' },
  ],
  // design.md §21: staged with a caveat and a prefix so the page-view capture
  // exercises a real label rather than the shortest possible one. `label` is
  // the server's formatting — the SPA never composes it (§21.4).
  marking: { level: 'SECRET', levelName: 'SECRET', eyesOnly: ['UK', 'US'], ukPrefix: true, selectors: [], label: 'UK SECRET UK/US EYES ONLY' },
  properties: [
    { keyId: 'k-owner', key: 'Owner', value: 'Ada Lovelace', sortOrder: 0 },
    { keyId: 'k-review', key: 'Review Date', value: '2026-11-01', sortOrder: 1 },
    { keyId: 'k-status', key: 'Status', value: 'In review', sortOrder: 2 },
  ],
  parent: { id: 'page-0', title: 'Static fire campaign', slug: 'static-fire-campaign' },
  children: [
    { id: 'page-2', title: 'Telemetry review notes', slug: 'telemetry-review-notes' },
    { id: 'page-3', title: 'Chill-in procedure v3', slug: 'chill-in-procedure-v3' },
  ],
  comments: [
    {
      id: 'c1',
      parentCommentId: null,
      body: 'The PT-201 sag matches the qual stand in March — pulling those runs for comparison :rocket:',
      isDeleted: false,
      authorUserId: 'user-ada',
      author: { id: 'user-ada', displayName: 'Ada Lovelace', hasAvatar: true },
      createdAtUtc: '2026-08-21T09:14:00Z',
      editedAtUtc: null,
    },
    {
      id: 'c2',
      parentCommentId: 'c1',
      body: 'Pulled them — same signature, smaller amplitude. Linked on the child page.',
      isDeleted: false,
      authorUserId: 'user-grace',
      author: { id: 'user-grace', displayName: 'Grace Hopper', hasAvatar: true },
      createdAtUtc: '2026-08-21T11:02:00Z',
      editedAtUtc: null,
    },
  ],
  attachments: [
    {
      id: 'att-1',
      fileName: 'static-fire-14aug-summary.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1_834_022,
      uploadedBy: { id: 'user-ada', displayName: 'Ada Lovelace', hasAvatar: true },
    },
  ],
  parentDenial: null,
  // What the three `page://` links in the content resolved to (§21.8).
  linkTargets: [
    { id: 'page-3', page: { id: 'page-3', title: 'Chill-in procedure v3', slug: 'chill-in-procedure-v3', spaceKey: 'PROP' }, denial: null },
    { id: 'page-4', page: null, denial: { placeholderTitle: '(protected)', noSpaceAccess: false, marking: { label: 'UK TOP SECRET UK EYES ONLY' } } },
    { id: 'page-9', page: null, denial: null },
  ],
}

/**
 * A page this caller may not read (design.md §6.7 / §21.8): the marking and
 * every failing gate, never a title. Staged with the widest label the
 * scheme can build and three reasons, so the protected screen and the tree
 * leaf both show the fullest shape in both themes.
 */
const PROTECTED_DENIAL = {
  placeholderTitle: '(protected)',
  noSpaceAccess: false,
  marking: {
    level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: ['NZ', 'US'], ukPrefix: true,
    selectors: [{ category: 'FRUIT', value: 'BANANA' }, { category: 'REGION', value: 'NORTH' }],
    label: 'UK TOP SECRET BANANA NORTH NZ/US EYES ONLY',
  },
  reasons: [
    { gate: 'CLASSIFICATION', passed: false, requiredLevelName: 'TOP SECRET' },
    { gate: 'SELECTOR_GRANT', passed: false, category: 'FRUIT', value: 'BANANA' },
    { gate: 'NATIONAL_CAVEAT', passed: false, countries: ['NZ', 'US'] },
  ],
}

/** The withheld shape: no access grant in the space, so the marking is withheld and only the space sentence remains. */
const NO_SPACE_DENIAL = {
  placeholderTitle: '(protected)',
  noSpaceAccess: true,
  marking: null,
  reasons: [{ gate: 'SPACE_ACCESS', passed: false }],
}

const spaces = [
  { id: 'space-eng', key: 'PROP', name: 'Propulsion', description: 'Engines, test stands, anomalies', isReplica: false, originInstanceId: 'LOW' },
  { id: 'space-av', key: 'AV', name: 'Avionics', description: 'Flight computers and harnessing', isReplica: false, originInstanceId: 'LOW' },
  { id: 'space-mirror', key: 'RANGE', name: 'Range Safety (mirror)', description: null, isReplica: true, originInstanceId: 'RANGE-LOW' },
]

// Three different levels across the tree so the space-browser capture puts
// three of the four marking tones (§21.1's ladder) in front of the browser
// layer's contrast check in both themes.
const spaceTreeNodes = [
  {
    __typename: 'PageTreeNode',
    id: 'page-1',
    title: 'Stage two ignition anomaly review',
    slug: 'stage-two-ignition-anomaly',
    // One node with an icon and two without: the tree's fallback glyph and a
    // page's own have to hold the same row height and alignment.
    icon: 'ROCKET',
    sortOrder: 0,
    hasRestrictions: false,
    labels: ['anomaly'],
    marking: { level: 'SECRET', levelName: 'SECRET', eyesOnly: ['UK', 'US'], ukPrefix: true, selectors: [], label: 'UK SECRET UK/US EYES ONLY' },
    children: [
      {
        __typename: 'PageTreeNode',
        id: 'page-4',
        title: 'Export-controlled test data',
        slug: 'export-controlled-test-data',
        sortOrder: 0,
        hasRestrictions: true,
        labels: [],
        marking: { level: 'TOP_SECRET', levelName: 'TOP SECRET', eyesOnly: ['UK'], ukPrefix: true, selectors: [], label: 'UK TOP SECRET UK EYES ONLY' },
        children: [],
      },
      // A withheld page at its sibling position (design.md §21.8): the
      // placeholder title, the whole marking label as a chip, and one
      // disclosure — the browser tier judges the chip and the muted row in
      // both themes.
      { __typename: 'ProtectedTreeNode', title: '(protected)', sortOrder: 1, denial: PROTECTED_DENIAL },
    ],
  },
  { __typename: 'PageTreeNode', id: 'page-3', title: 'Chill-in procedure v3', slug: 'chill-in-procedure-v3', sortOrder: 1, hasRestrictions: false, labels: [], marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' }, children: [] },
]

/** One unchanged line and one that moves, so the history capture has both fills. */
const HISTORY_BODY = (cause: string) => `Stage two ignition held at T-4 seconds.\n\n${cause}\n`

function mockClient() {
  return createMockUrqlClient((name, op) => {
    if (name === 'PageById') return { page }
    // The disclosing read (§6.7 / §21.8): the staged page, or one of the two
    // denial shapes, by id — so the protected captures come from the same
    // route and screen a real reader would hit.
    if (name === 'PageAccessById') {
      const id = (op.variables as { id: string }).id
      if (id === 'page-protected') return { pageAccess: { page: null, denial: PROTECTED_DENIAL } }
      if (id === 'page-nospace') return { pageAccess: { page: null, denial: NO_SPACE_DENIAL } }
      return { pageAccess: { page, denial: null } }
    }
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-chris', email: 'chris@rocketwiki.dev', name: 'Chris', groups: ['propulsion'],
          isAuthenticated: true, isInstanceAdmin: true, localUserId: 'user-chris', hasAvatar: true,
          // §21.6: SECRET clearance leaves TOP_SECRET visibly unavailable in
          // the marking control, which is the state worth capturing — the
          // "Above your clearance" reason has to be readable in both themes.
          clearance: 'SECRET', nationality: ['UK'],
          // §21.15: eligible for FRUIT (the token says so) and REGION (no
          // attribute gate); the marking control's pickers are both live.
          selectorEligibility: ['FRUIT', 'REGION'],
        },
      }
    if (name === 'SpaceReplicaBanner')
      return { space: { id: 'space-eng', key: 'PROP', isReplica: false, originInstanceId: 'LOW' } }
    if (name === 'SpaceList') return { spaces }
    // What a `/pages/{id}` route resolves its space from — the drawer's tree
    // and the header breadcrumb both ask, and without it those captures audit
    // the "space not resolved yet" state rather than the one users see.
    if (name === 'PageSpaceRef') return { page: { id: 'page-1', spaceId: 'space-eng', spaceKey: 'PROP' } }
    if (name === 'PersistedNotifications')
      return {
        notifications: [
          { id: 'n1', type: 'mention', pageId: 'page-1', spaceKey: 'PROP', pageTitle: 'Stage two ignition anomaly review', actorDisplayName: 'Ada Lovelace', createdAtUtc: '2026-08-23T08:30:00Z', readAtUtc: null },
          { id: 'n2', type: 'page_watched_changed', pageId: 'page-2', spaceKey: 'PROP', pageTitle: 'Telemetry review notes', actorDisplayName: 'Grace Hopper', createdAtUtc: '2026-08-22T15:04:00Z', readAtUtc: '2026-08-22T16:00:00Z' },
        ],
      }
    if (name === 'SpaceTreeForMove') return { pageTree: [] }
    if (name === 'SpaceLabelDetails') return { labelDetails: page.labelDetails }
    if (name === 'CustomEmojis')
      return { customEmojis: [{ name: 'rocket', etag: '"r1"' }, { name: 'banana', etag: '"b1"' }] }
    if (name === 'PageHistory')
      return {
        page: {
          id: page.id,
          title: page.title,
          canEdit: true,
          // Three revisions with two different authors and one co-edited save,
          // so the capture exercises the avatar, the "with …" caption, and the
          // em dash that stands in for a missing summary — plus a real diff.
          revisions: [
            { id: 'rev-1', revisionNumber: 1, title: page.title, content: HISTORY_BODY('Root cause unknown.'), editSummary: null, createdAtUtc: '2026-08-20T09:15:00Z', author: { id: 'user-ada', displayName: 'Ada Lovelace', hasAvatar: false }, contributors: [] },
            { id: 'rev-2', revisionNumber: 2, title: page.title, content: HISTORY_BODY('Root cause: chill-in valve lag.'), editSummary: 'Recorded the root cause', createdAtUtc: '2026-08-22T14:02:00Z', author: { id: 'user-grace', displayName: 'Grace Hopper', hasAvatar: false }, contributors: [] },
            { id: 'rev-3', revisionNumber: 3, title: page.title, content: HISTORY_BODY('Root cause: chill-in valve lag on the LOX side.'), editSummary: 'Narrowed it to the LOX side', createdAtUtc: '2026-08-23T08:40:00Z', author: { id: 'user-chris', displayName: 'Chris', hasAvatar: true }, contributors: [{ id: 'user-ada', displayName: 'Ada Lovelace', hasAvatar: false }, { id: 'user-chris', displayName: 'Chris', hasAvatar: true }] },
          ],
        },
      }
    if (name === 'PagePropertiesForPage')
      // `canManageAccess: true` so the capture includes the "Placement and
      // access" section — Move and Permissions moved onto this screen, and an
      // unstaged flag would drop both buttons out of the contrast and
      // target-size checks entirely.
      return {
        page: {
          id: page.id,
          title: page.title,
          spaceKey: page.spaceKey,
          spaceId: page.spaceId,
          canEdit: true,
          canManageAccess: true,
          marking: page.marking,
          properties: page.properties,
        },
      }
    if (name === 'PagePropertyKeys')
      return {
        pagePropertyKeys: [
          { id: 'k-owner', key: 'Owner', description: 'Who to ask about this page.', sortOrder: 0 },
          { id: 'k-review', key: 'Review Date', description: 'When this page is next reviewed.', sortOrder: 1 },
          { id: 'k-status', key: 'Status', description: null, sortOrder: 2 },
          { id: 'k-classification', key: 'Classification', description: 'Export-control marking.', sortOrder: 3 },
        ],
      }
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured: true, baseUrl: 'https://gitlab.internal', viewerHasToken: true } }
    if (name === 'SearchPages')
      return {
        search: {
          // §21.13: the aggregate spans the whole permission-filtered hit set,
          // so it legitimately out-ranks the three rows rendered here — which
          // is exactly the state worth capturing, banner plus scope note.
          aggregateMarking: { level: 'SECRET', label: 'UK SECRET UK/US EYES ONLY' },
          totalCount: 12,
          pageInfo: { hasNextPage: true, endCursor: 'c10' },
          edges: [
            { cursor: 'c1', node: { snippet: '…showed a 270 ms ignition delay on the stage two vacuum engine…', headingPath: ['Stage two ignition anomaly review'], anchorId: 'stage-two-ignition-anomaly-review', page: { id: 'page-1', title: 'Stage two ignition anomaly review', spaceKey: 'PROP', marking: page.marking } } },
            { cursor: 'c2', node: { snippet: '…igniter feed line transient is visible on the unfiltered channel…', headingPath: ['Findings', 'Igniter feed'], anchorId: 'igniter-feed', page: { id: 'page-2', title: 'Telemetry review notes', spaceKey: 'PROP', marking: { level: 'OFFICIAL_SENSITIVE', levelName: 'OFFICIAL-SENSITIVE', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL-SENSITIVE' } } } },
            { cursor: 'c3', node: { snippet: '…extended pre-press hold keeps PT-201 above the redline through ignition…', headingPath: ['Chill-in procedure', 'Pre-press'], anchorId: 'pre-press', page: { id: 'page-3', title: 'Chill-in procedure v3', spaceKey: 'PROP', marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: false, selectors: [], label: 'OFFICIAL' } } } },
          ],
        },
      }
    if (name === 'SearchFacets')
      return { spaces: spaces.map((s) => ({ key: s.key, name: s.name })), labels: ['anomaly', 'propulsion', 'ops'] }
    if (name === 'PageListQuery')
      return {
        pageQuery: {
          // §21.13 again, but on a surface nothing else in this set covers: an
          // aggregate banner and marking badges rendered inside EDITOR CONTENT,
          // which is plain CSS with its own theme variables rather than the MUI
          // page chrome the search capture exercises.
          aggregateMarking: { level: 'SECRET', label: 'UK SECRET UK/US EYES ONLY' },
          // Deliberately more than the two rows shown, so the capture includes
          // the "Showing the first N of M" line — the sentence §6.7 constrains
          // most tightly, and the one worth having a human look at.
          totalCount: 9,
          errors: [],
          edges: [
            { cursor: 'q1', node: { page: { id: 'page-1', title: 'Stage two ignition anomaly review', spaceKey: 'PROP', marking: page.marking } } },
            { cursor: 'q2', node: { page: { id: 'page-3', title: 'Chill-in procedure v3', spaceKey: 'PROP', marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' } } } },
          ],
          pageInfo: { hasNextPage: true, endCursor: 'q2' },
        },
      }
    if (name === 'AskWiki')
      return {
        askWiki: {
          answer:
            'The 270 ms delay traces to turbopump inlet pressure sagging below the chill-in redline [S1]. ' +
            'The igniter feed transient was masked by the telemetry filter — the unfiltered channel confirms it [S2].',
          citations: [
            { pageId: 'page-1', title: 'Stage two ignition anomaly review', headingPath: ['Findings so far'], anchorId: 'findings-so-far', marking: page.marking },
            { pageId: 'page-2', title: 'Telemetry review notes', headingPath: ['Findings', 'Igniter feed'], anchorId: 'igniter-feed', marking: { level: 'OFFICIAL_SENSITIVE', levelName: 'OFFICIAL-SENSITIVE', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL-SENSITIVE' } },
          ],
          unavailable: null,
          // §21.13's conjunctive caveat — distinct source eyes-only sets are
          // LISTED, never unioned or intersected. This is the widest marking
          // string the app can display and it exists nowhere else in the
          // capture set, so this is where the browser tier gets to check it
          // for contrast and for wrapping/overflow in both themes. It also
          // out-ranks both citation badges below it, which is the honest
          // rendering of "the aggregate covers uncited context too".
          aggregateMarking: { level: 'SECRET', label: 'UK SECRET NZ EYES ONLY, US EYES ONLY' },
        },
      }
    if (name === 'SpaceTree')
      return {
        space: {
          id: 'space-eng', key: 'PROP', name: 'Propulsion', description: 'Engines, test stands, anomalies',
          homepageId: null, isReplica: false, originInstanceId: 'LOW', viewerIsWatching: true,
          canManageAccess: true, viewerHasAccess: true,
        },
      }
    if (name === 'SpacePageTree') return { pageTree: spaceTreeNodes }
    // §21.15: the dev catalog, and the caller's grants in this space — APPLE
    // but not BANANA, so the marking control captures a greyed value with
    // its reason beside an offered one.
    if (name === 'SelectorCategories')
      return {
        selectorCategories: [
          { name: 'FRUIT', description: 'Fruit programme compartments', requiresAttribute: true, values: ['APPLE', 'BANANA'] },
          { name: 'REGION', description: 'Regional releasability', requiresAttribute: false, values: ['NORTH', 'SOUTH'] },
        ],
      }
    if (name === 'SpaceSelectorGrants')
      return {
        space: {
          id: 'space-eng', key: 'PROP',
          viewerSelectorGrants: [{ category: 'FRUIT', value: 'APPLE' }, { category: 'REGION', value: 'NORTH' }, { category: 'REGION', value: 'SOUTH' }],
        },
      }
    // The form blocks: a definition plus records at two different markings, so the
    // browser tier judges the table's badges as well as the form controls.
    if (name === 'PageForms')
      return {
        pageForms: {
          definitions: [
            {
              collection: 'incident-report',
              fields: [
                { name: 'severity', type: 'SELECT', required: true, options: ['low', 'medium', 'high'] },
                { name: 'summary', type: 'TEXT', required: true, options: [] },
                { name: 'occurredAt', type: 'DATE', required: false, options: [] },
              ],
            },
          ],
          errors: [],
        },
      }
    if (name === 'PageEntries')
      return {
        pageEntries: [
          {
            id: 'entry-1',
            collection: 'incident-report',
            data: JSON.stringify({ severity: 'high', summary: 'Turbopump inlet pressure sagged', occurredAt: '2026-08-14' }),
            version: 1,
            createdAtUtc: '2026-08-14T00:00:00Z',
            marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], ukPrefix: true, selectors: [], label: 'UK OFFICIAL' },
          },
          {
            id: 'entry-2',
            collection: 'incident-report',
            data: JSON.stringify({ severity: 'low', summary: 'Igniter feed transient', occurredAt: '2026-08-15' }),
            version: 1,
            createdAtUtc: '2026-08-15T00:00:00Z',
            marking: { level: 'SECRET', levelName: 'SECRET', eyesOnly: ['UK'], ukPrefix: true, selectors: [], label: 'UK SECRET UK EYES ONLY' },
          },
        ],
      }
    // Analytics is the one screen whose whole content is colour-coded marks, so
    // the browser tier (which alone can judge contrast on painted pixels) needs a
    // populated report rather than an empty state.
    if (name === 'Analytics')
      return {
        analytics: {
          scope: { spaceKey: 'PROP', fromUtc: '2026-08-01T00:00:00Z', toUtc: '2026-08-31T00:00:00Z', visiblePageCount: 17 },
          activity: Array.from({ length: 14 }, (_, i) => ({
            day: `2026-08-${String(i + 1).padStart(2, '0')}`,
            views: [12, 18, 9, 24, 31, 6, 4, 22, 27, 19, 14, 30, 25, 11][i],
            edits: [2, 5, 1, 7, 4, 0, 0, 6, 3, 2, 5, 8, 4, 1][i],
          })),
          mostViewed: [
            { pageId: 'p1', title: 'Stage two ignition anomaly review', slug: 'stage-two', spaceKey: 'PROP', count: 212 },
            { pageId: 'p2', title: 'Turbopump chill-in procedure', slug: 'chill-in', spaceKey: 'PROP', count: 148 },
          ],
          mostEdited: [{ pageId: 'p2', title: 'Turbopump chill-in procedure', slug: 'chill-in', spaceKey: 'PROP', count: 24 }],
          topReaders: [
            { userId: 'u1', displayName: 'Ada Lovelace', count: 96 },
            { userId: 'u2', displayName: 'Grace Hopper', count: 74 },
          ],
          topContributors: [{ userId: 'u2', displayName: 'Grace Hopper', count: 24 }],
          health: {
            stalePageCount: 3,
            orphanPageCount: 2,
            unlabelledPageCount: 6,
            neverViewedPageCount: 4,
            stalestPages: [{ pageId: 'p3', title: 'Legacy igniter notes', slug: 'legacy-igniter', spaceKey: 'PROP', count: 412 }],
          },
          topSearches: [{ query: 'chill-in', runCount: 31, zeroResultCount: 0 }],
          zeroResultSearches: [{ query: 'pre-press hold', runCount: 9, zeroResultCount: 9 }],
        },
      }
    if (name === 'SpaceTrash')
      return {
        space: {
          id: 'space-eng',
          key: 'PROP',
          name: 'Propulsion',
          trashedPages: [
            { id: 'page-9', title: 'Legacy igniter notes', parentPageId: null, ancestorPath: [], deleteBatchId: 'batch-1', deletedAtUtc: '2026-08-10T09:00:00Z', deletedBy: { id: 'user-ada', displayName: 'Ada Lovelace', hasAvatar: true } },
            { id: 'page-10', title: 'Legacy igniter appendix', parentPageId: 'page-9', ancestorPath: ['page-9'], deleteBatchId: 'batch-1', deletedAtUtc: '2026-08-10T09:00:00Z', deletedBy: { id: 'user-ada', displayName: 'Ada Lovelace', hasAvatar: true } },
            { id: 'page-11', title: 'Old chill-in checklist', parentPageId: null, ancestorPath: [], deleteBatchId: 'batch-2', deletedAtUtc: '2026-08-18T14:30:00Z', deletedBy: { id: 'user-grace', displayName: 'Grace Hopper', hasAvatar: true } },
          ],
        },
      }
    if (name === 'AuditEvents')
      return {
        auditEvents: {
          totalCount: 3,
          pageInfo: { hasNextPage: false, endCursor: null },
          nodes: [
            { id: 'a1', timestampUtc: '2026-08-23T10:14:00Z', userId: 'user-ada', userDisplayName: 'Ada Lovelace', action: 'page.view', subjectType: 'PAGE', subjectId: 'page-1', spaceKey: 'PROP', outcome: 'SUCCESS', channel: 'WEB', mcpClient: null, detailsJson: null },
            { id: 'a2', timestampUtc: '2026-08-23T10:15:20Z', userId: 'user-grace', userDisplayName: 'Grace Hopper', action: 'page.view', subjectType: 'PAGE', subjectId: 'page-4', spaceKey: 'PROP', outcome: 'DENIED', channel: 'WEB', mcpClient: null, detailsJson: null },
            { id: 'a3', timestampUtc: '2026-08-23T11:02:11Z', userId: null, userDisplayName: null, action: 'space.sync', subjectType: 'SPACE', subjectId: 'space-mirror', spaceKey: 'RANGE', outcome: 'SUCCESS', channel: 'SYNC', mcpClient: null, detailsJson: null },
          ],
        },
      }
    if (name === 'PagePermissions')
      return {
        page: {
          id: 'page-1',
          title: 'Stage two ignition anomaly review',
          spaceKey: 'PROP',
          canManageAccess: true,
          restrictions: [
            { ruleId: 'rule-own', pageId: 'page-1', pageTitle: 'Stage two ignition anomaly review', inherited: false, action: 'VIEW', expressionJson: '{"group":"export-cleared"}', createdAtUtc: '2026-08-01T00:00:00Z', updatedAtUtc: '2026-08-01T00:00:00Z', updatedByDisplayName: 'Ada Lovelace' },
            { ruleId: 'rule-inh', pageId: 'page-0', pageTitle: 'Static fire campaign', inherited: true, action: 'VIEW', expressionJson: '{"group":"propulsion"}', createdAtUtc: '2026-07-01T00:00:00Z', updatedAtUtc: '2026-07-01T00:00:00Z', updatedByDisplayName: null },
          ],
        },
      }
    if (name === 'ClassificationScheme')
      // §21.1's display spellings, in scheme order — the picker's option
      // labels, so the capture pins the hyphenated UK form rather than the
      // wire name.
      return {
        classificationScheme: [
          { level: 'OFFICIAL', name: 'OFFICIAL' },
          { level: 'OFFICIAL_SENSITIVE', name: 'OFFICIAL-SENSITIVE' },
          { level: 'SECRET', name: 'SECRET' },
          { level: 'TOP_SECRET', name: 'TOP SECRET' },
        ],
      }
    if (name === 'RuleVocabulary')
      return {
        groups: ['propulsion', 'export-cleared'],
        attributeRegistry: [
          { key: 'clearance', displayName: 'Clearance', allowedValues: ['itar', 'public'] },
          // §21.4: the eyes-only picker's vocabulary is this attribute's
          // allowedValues and nothing else — there is no ISO list.
          { key: 'nationality', displayName: 'Nationality', allowedValues: ['UK', 'US', 'AU'] },
        ],
      }
    if (name === 'EffectivePermission')
      return {
        effectivePermission: {
          userId: 'sub-chris', userDisplayName: 'Chris', hasSpaceAccess: true, spaceRole: 'SPACE_ADMIN', isReplicaSpace: false,
          canView: true, canEdit: true, viewDenialReason: null, editDenialReason: null,
          // The whole ladder, every gate evaluated (§6.6): pass and fail icons
          // both reach the contrast check.
          viewGates: [
            { gate: 'SPACE_ACCESS', passed: true },
            { gate: 'CLASSIFICATION', passed: true },
            { gate: 'SELECTOR_ELIGIBILITY', passed: true, category: 'FRUIT' },
            { gate: 'SELECTOR_GRANT', passed: true, category: 'FRUIT', value: 'APPLE' },
            { gate: 'NATIONAL_CAVEAT', passed: true },
            { gate: 'RESTRICTION', passed: true },
          ],
          editGates: [
            { gate: 'REPLICA', passed: true },
            { gate: 'ROLE', passed: true, requiredRole: 'EDITOR' },
          ],
          viewRestrictions: [], editRestrictions: [],
        },
      }
    return undefined
  })
}

type Mode = 'light' | 'dark'

function shell(mode: Mode, initialPath: string, routePath: string, element: React.ReactElement) {
  window.localStorage.setItem('rocketwiki:color-mode', mode)
  const router = createMemoryRouter(
    [{ path: '/', element: <AppShell />, children: [{ path: routePath, element }] }],
    { initialEntries: [initialPath] },
  )
  return (
    <ColorModeProvider>
      <UrqlProvider value={mockClient().client}>
        <RouterProvider router={router} />
      </UrqlProvider>
    </ColorModeProvider>
  )
}

function standalone(mode: Mode, element: React.ReactElement) {
  window.localStorage.setItem('rocketwiki:color-mode', mode)
  return <ColorModeProvider>{element}</ColorModeProvider>
}

const settle = () => new Promise((r) => setTimeout(r, 400))

interface Screen {
  /** File stem — becomes `<stem>--<mode>.html`. */
  name: string
  render: (mode: Mode) => React.ReactElement
  /** Post-render staging (open menus, type questions, emit presence…). */
  stage?: () => Promise<void> | void
}

/**
 * Labels as `ProtectiveMarking.Format` would build them (design.md §21.12's
 * four-combination table), paired with the level's own display spelling as
 * `ProtectiveMarking.LevelName` gives it (§21.1). TOP_SECRET is staged
 * without a prefix on purpose: that is a legal marking, and it is also what
 * the server's fail-closed substitute renders when a marking row is missing.
 */
const STAGED_MARKINGS: Record<(typeof CLASSIFICATION_LADDER)[number], { label: string; levelName: string }> = {
  OFFICIAL: { label: 'UK OFFICIAL', levelName: 'OFFICIAL' },
  OFFICIAL_SENSITIVE: { label: 'UK OFFICIAL-SENSITIVE', levelName: 'OFFICIAL-SENSITIVE' },
  SECRET: { label: 'UK SECRET UK/US EYES ONLY', levelName: 'SECRET' },
  TOP_SECRET: { label: 'TOP SECRET', levelName: 'TOP SECRET' },
}

const restrictedRule = {
  ruleId: 'export-control',
  pageId: 'restricted-parent',
  pageTitle: 'Export-Controlled Docs',
  action: 'view' as const,
  expression: group('export-cleared'),
}

const SCREENS: Screen[] = [
  {
    name: 'page-view',
    render: (mode) => shell(mode, '/pages/page-1', 'pages/:pageId', <PageViewPage />),
    stage: async () => {
      // Live presence viewers (the header avatar strip). Deliberately NO
      // staged pointers: the pointer overlay is a full-bleed positioned
      // layer, and anything axe sees overlapping text makes it ABSTAIN from
      // contrast checks for the whole page underneath — one staged pointer
      // would silently blind 1.4.3 coverage of the entire capture. Pointer
      // LABEL contrast is guaranteed by construction instead:
      // presence/readableTextOn.ts + its exhaustive contrast tests.
      const presence = realtime.presence as FakePresenceTransport
      act(() => {
        presence.emitViewers([
          { userId: 'user-ada', displayName: 'Ada Lovelace', colour: 'hsl(60, 70%, 45%)', hasAvatar: false },
          { userId: 'user-grace', displayName: 'Grace Hopper', colour: 'hsl(174, 70%, 45%)', hasAvatar: false },
          { userId: 'user-chris', displayName: 'Chris', colour: 'hsl(240, 70%, 45%)', hasAvatar: false },
        ])
      })
      await settle()
    },
  },
  {
    // The full editor surface: formatting toolbar (icon buttons, toggle
    // groups), TipTap content with tables/callouts/code, and the save bar —
    // the browser layer's contrast + target-size coverage for the editor.
    name: 'page-edit',
    render: (mode) => shell(mode, '/pages/page-1/edit', 'pages/:pageId/edit', <PageEditPage />),
  },
  { name: 'search', render: (mode) => shell(mode, '/search?q=ignition', 'search', <SearchPage />) },
  { name: 'settings', render: (mode) => shell(mode, '/settings', 'settings', <SettingsPage />) },
  {
    name: 'ask',
    render: (mode) => shell(mode, '/ask', 'ask', <AskWikiPage />),
    stage: async () => {
      const box = document.querySelector('textarea:not([aria-hidden])')
      if (box) {
        fireEvent.change(box, { target: { value: 'Why did the stage two ignition delay?' } })
        fireEvent.keyDown(box, { key: 'Enter', code: 'Enter' })
      }
      await settle()
    },
  },
  { name: 'admin-emojis', render: (mode) => shell(mode, '/admin/emojis', 'admin/emojis', <AdminEmojisPage />) },
  {
    // Both property screens render fully from the same staged mock the rest
    // of this file uses — a table of value fields plus a picker on one, a
    // DataGrid plus a create form on the other — so they cost one mock entry
    // each and earn the browser layer's contrast/target-size coverage.
    name: 'page-details',
    render: (mode) => shell(mode, '/pages/page-1/details', 'pages/:pageId/details', <PageDetailsPage />),
  },
  {
    // A dense table with two radio columns and a diff panel below it — the two
    // things worth a real browser here are the radios' 2.5.8 target size and
    // the added/removed fills, which are alpha-composited over the surface and
    // so have a different contrast story in each theme.
    name: 'page-history',
    render: (mode) => shell(mode, '/pages/page-1/history', 'pages/:pageId/history', <PageHistoryPage />),
  },
  {
    name: 'admin-property-keys',
    render: (mode) => shell(mode, '/admin/property-keys', 'admin/property-keys', <AdminPropertyKeysPage />),
  },
  {
    name: 'permissions',
    render: (mode) => shell(mode, '/pages/page-1/permissions', 'pages/:pageId/permissions', <PagePermissionsPage />),
  },
  {
    // The browser with a protected leaf disclosed, so the reasons list under
    // the placeholder — muted caption text over the page surface — is judged
    // for contrast in both themes, not only the collapsed row.
    name: 'space-browser',
    render: (mode) => shell(mode, '/spaces/PROP/-/browse', 'spaces/:spaceKey/-/browse', <SpaceBrowserPage />),
    stage: async () => {
      fireEvent.click(await screen.findByRole('button', { name: 'Why is this page protected?' }))
      await settle()
    },
  },
  // The two shapes of a withheld page (design.md §6.7 / §21.8), through the
  // real route: the marking banner and the reasons list, and the withheld
  // shape with only the space sentence.
  { name: 'protected-page', render: (mode) => shell(mode, '/pages/page-protected', 'pages/:pageId', <PageViewPage />) },
  {
    name: 'protected-page-no-space-access',
    render: (mode) => shell(mode, '/pages/page-nospace', 'pages/:pageId', <PageViewPage />),
  },
  { name: 'analytics', render: (mode) => shell(mode, '/spaces/PROP/-/analytics', 'spaces/:spaceKey/-/analytics', <AnalyticsPage />) },
  // Help renders long prose through the read-only editor, so this is also the
  // capture that would catch a body-copy contrast regression in either theme.
  { name: 'help', render: (mode) => shell(mode, '/-/docs/classification', '-/docs/:topic', <HelpPage />) },
  // Through the shell rather than standalone: the blocks query, so they need the
  // urql provider the shell supplies. Mounted directly rather than inside the
  // editor, because staging a real fence would capture TipTap's chrome instead of
  // the thing under test.
  {
    name: 'forms',
    render: (mode) =>
      shell(
        mode,
        '/pages/page-1',
        'pages/:pageId',
        <PageIdContext value="page-1">
          <FormDefinitionBlock collection="incident-report" />
          <FormListBlock collection="incident-report" columns={[]} />
        </PageIdContext>,
      ),
  },
  { name: 'trash', render: (mode) => shell(mode, '/spaces/PROP/-/trash', 'spaces/:spaceKey/-/trash', <TrashPage />) },
  { name: 'audit-log', render: (mode) => shell(mode, '/admin/audit', 'admin/audit', <AuditLogPage />) },
  // The LANDING page — the first screen every user sees, and the only one
  // rendering the replica chip inside a card. It had no capture at all, so
  // none of that was ever contrast- or target-size-checked.
  { name: 'space-list', render: (mode) => shell(mode, '/', '/', <SpaceListPage />) },
  // Carries the 'Planned' treatment for the not-built-yet sections, which is a
  // dashed border plus a chip — exactly the kind of thing worth painting.
  { name: 'admin', render: (mode) => shell(mode, '/admin', 'admin', <AdminPage />) },
  {
    name: 'notification-bell-open',
    render: (mode) => shell(mode, '/pages/page-1', 'pages/:pageId', <PageViewPage />),
    stage: async () => {
      fireEvent.click(await screen.findByRole('button', { name: /^Notifications \(/ }))
      await settle()
    },
  },
  {
    name: 'move-dialog',
    render: (mode) =>
      standalone(
        mode,
        <MovePageDialog
          open
          onClose={() => {}}
          pageTitle="Stage two ignition anomaly review"
          currentAncestorRestrictions={[]}
          targetOptions={[
            { id: 'open-parent', title: 'Open Parent', ancestorRestrictions: [] },
            { id: 'restricted-parent', title: 'Export-Controlled Docs', ancestorRestrictions: [restrictedRule] },
          ]}
          onConfirm={() => {}}
        />,
      ),
    stage: async () => {
      // Select the restricted target so the visibility-change warning shows.
      const input = screen.getByLabelText('New parent')
      fireEvent.mouseDown(input)
      fireEvent.change(input, { target: { value: 'Export-Controlled' } })
      fireEvent.click(screen.getByText('Export-Controlled Docs'))
      await settle()
    },
  },
  {
    // Every rung of §21.1's ladder, in one capture, because the page-view and
    // page-properties screens can only ever stage ONE marking each — and the
    // whole point of markingTone.ts is that all four tones stay readable in
    // both themes. Real components, staged with labels shaped exactly as the
    // server's one formatter builds them (§21.12's table), including the
    // legal no-prefix case at the top of the ladder.
    name: 'marking-levels',
    render: (mode) =>
      standalone(
        mode,
        <Box sx={{ p: 3, display: 'grid', gap: 3, maxWidth: 720 }}>
          {CLASSIFICATION_LADDER.map((level) => (
            <Box key={level} sx={{ display: 'grid', gap: 1 }}>
              <MarkingBanner level={level} placement="head" label={STAGED_MARKINGS[level].label} />
              <Box>
                <MarkingLevelBadge level={level} levelName={STAGED_MARKINGS[level].levelName} />
              </Box>
            </Box>
          ))}
        </Box>,
      ),
  },
  {
    // The page-list widget (design.md §22) rendered in READ mode, through the
    // real editor. It earns its own capture rather than riding on page-view:
    // it is the only place in the app where MUI marking components (an
    // aggregate banner and level badges) sit inside editor-content's plain-CSS
    // surface, so it is the only place where those two colour systems meet —
    // and that meeting is exactly what a contrast check in both themes is for.
    // Its own <ul> row list, link colour and muted space text are likewise not
    // covered by any other screen.
    name: 'page-list-widget',
    render: (mode) =>
      standalone(
        mode,
        <UrqlProvider value={mockClient().client}>
          <Box sx={{ p: 3, maxWidth: 720 }}>
            <RichTextEditor
              initialMarkdown={
                '```page-list\nquery = label = "anomaly" AND space IN ("PROP")\nlimit = 2\n```\n'
              }
              editable={false}
              showToolbar={false}
            />
          </Box>
        </UrqlProvider>,
      ),
  },
  {
    name: 'stale-revision-dialog',
    render: (mode) =>
      standalone(
        mode,
        <StaleRevisionDialog
          open
          currentRevisionNumber={7}
          yourTitle="Stage two ignition anomaly review"
          yourDraft={'shared context line\nmy corrective action\n'}
          theirTitle="Stage two ignition anomaly review (v2)"
          theirContent={'shared context line\ntheir corrective action\n'}
          onOverwriteAnyway={() => {}}
          onCopyAndCancel={() => {}}
          onKeepEditing={() => {}}
        />,
      ),
  },
]

const captured: { file: string; html: string; styles: string }[] = []

for (const mode of ['light', 'dark'] as const) {
  for (const spec of SCREENS) {
    it(
      `${spec.name} (${mode})${mode === 'light' ? ' — jsdom axe pass' : ''}`,
      async () => {
        setEmojiRegistry([
          { name: 'rocket', etag: '"r1"' },
          { name: 'banana', etag: '"b1"' },
        ])
        render(spec.render(mode))
        await settle()
        await spec.stage?.()

        // Snapshot BOTH the DOM and the Emotion styles while mounted —
        // MUI's CssBaseline globals (body background/color per theme) are
        // removed on unmount, so a post-cleanup snapshot would strip the
        // dark theme's page background.
        captured.push({
          file: `${spec.name}--${mode}.html`,
          html: document.body.innerHTML, // body, not container: portals (menus/dialogs) live beside the root
          styles: Array.from(document.head.querySelectorAll('style'))
            .map((s) => s.outerHTML)
            .join('\n'),
        })

        if (mode === 'light') {
          await expectNoAxeViolations(document.body)
        }
        cleanup()
      },
      30_000,
    )
  }
}

afterAll(() => {
  const outDir = process.env.PREVIEW_OUT
  if (!outDir) return

  // Plain-CSS imports (Vite) don't run under vitest — embed the repo's real
  // stylesheets explicitly, same as captureScreens.test.tsx.
  const cssFiles = ['../index.css', '../editor/editor-content.css'].map((rel) =>
    readFileSync(new URL(rel, import.meta.url), 'utf8'),
  )
  mkdirSync(outDir, { recursive: true })
  // Clear the directory first. A capture that is renamed or retired otherwise
  // lingers in a developer's screens/ forever, because nothing ever deletes it
  // — so a local run accumulates files a clean CI checkout will never produce,
  // and the two disagree about how many screens exist. That is exactly how the
  // completeness assertion in a11y/a11y.spec.ts came to be calibrated against a
  // count only one machine could reach: page-properties was replaced by
  // page-details, its two files stayed behind locally, and CI failed on a
  // number that looked right here.
  for (const stale of readdirSync(outDir).filter((f) => f.endsWith('.html'))) {
    rmSync(join(outDir, stale))
  }
  for (const s of captured) {
    // data-theme mirrors what ColorModeProvider stamps on the live
    // documentElement — editor-content.css themes on it, and the attribute
    // lives OUTSIDE body.innerHTML, so the template must carry it.
    const mode = s.file.endsWith('--dark.html') ? 'dark' : 'light'
    writeFileSync(
      join(outDir, s.file),
      `<!doctype html><html lang="en" data-theme="${mode}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>RocketWiki — ${s.file.replace('.html', '')}</title><style>${cssFiles.join('\n')}</style>${s.styles}</head><body>${s.html}</body></html>`,
    )
  }
})
