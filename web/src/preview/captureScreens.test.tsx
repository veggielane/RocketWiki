// The README screenshot generator (docs/screenshots/*.png) — see the
// "Screenshots" note in the root README. This renders REAL components with
// staged sample data through the same mock seams the test suite uses, and
// writes one self-contained HTML file per screen; a headless browser then
// screenshots those (any browser works; the repo's images were produced with
// Playwright/Chromium — see docs/screenshots/README.md).
//
// In a normal test run (no PREVIEW_OUT env var) nothing is written — the
// renders still execute, so this doubles as a smoke test that the composed
// screens mount. With PREVIEW_OUT=<dir>, each screen's HTML lands there.
//
// Staged data only: no backend runs here. Avatars and emoji images are inline
// SVG data URIs standing in for the blob-URL caches (the real app fetches
// authenticated blobs — data URIs keep the captures self-contained).
import { mkdirSync, writeFileSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { it, vi } from 'vitest'
import { cleanup, render } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { Provider as UrqlProvider } from 'urql'
import { AppShell } from '../app/AppShell'
import { PageViewPage } from '../pages/PageViewPage'
import { SearchPage } from '../pages/SearchPage'
import { SettingsPage } from '../pages/SettingsPage'
import { AskWikiPage } from '../pages/AskWikiPage'
import { fireEvent } from '@testing-library/react'
import { ColorModeProvider } from '../theme/ColorModeProvider'
import { createMockUrqlClient } from '../test/mockUrqlClient'
import { setEmojiRegistry } from '../emoji/registry'

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

vi.mock('../realtime/transports', async () => {
  const { FakePresenceTransport } = await import('../realtime/FakePresenceTransport')
  const { FakeNotificationsTransport } = await import('../realtime/FakeNotificationsTransport')
  const presence = new FakePresenceTransport()
  const notifications = new FakeNotificationsTransport()
  return {
    getDefaultPresenceTransport: () => presence,
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
  'two vacuum engine. This page tracks the investigation.',
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
  // design.md §21.5: no page is unmarked, and §21.4 puts the one formatter
  // server-side — `label` is staged as the server would render it.
  marking: { level: 'SECRET', levelName: 'SECRET', eyesOnly: ['UK', 'US'], prefix: 'UK', label: 'UK SECRET [UK/US EYES ONLY]' },
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
}

const spaces = [
  { id: 'space-eng', key: 'PROP', name: 'Propulsion', description: 'Engines, test stands, anomalies', isReplica: false, originInstanceId: 'LOW' },
  { id: 'space-av', key: 'AV', name: 'Avionics', description: 'Flight computers and harnessing', isReplica: false, originInstanceId: 'LOW' },
  { id: 'space-mirror', key: 'RANGE', name: 'Range Safety (mirror)', description: null, isReplica: true, originInstanceId: 'RANGE-LOW' },
]

function mockClient() {
  return createMockUrqlClient((name) => {
    if (name === 'PageById') return { page }
    if (name === 'CurrentUser')
      return {
        me: {
          id: 'sub-chris', email: 'chris@rocketwiki.dev', name: 'Chris', groups: ['propulsion'],
          isAuthenticated: true, isInstanceAdmin: true, localUserId: 'user-chris', hasAvatar: true,
          clearance: 'SECRET', nationality: ['UK'],
        },
      }
    if (name === 'SpaceReplicaBanner')
      return { space: { id: 'space-eng', key: 'PROP', isReplica: false, originInstanceId: 'LOW' } }
    if (name === 'SpaceList') return { spaces }
    if (name === 'PersistedNotifications')
      return {
        notifications: [
          { id: 'n1', type: 'mention', pageId: 'page-1', spaceKey: 'PROP', pageTitle: 'Stage two ignition anomaly review', actorDisplayName: 'Ada Lovelace', createdAtUtc: '2026-08-23T08:30:00Z', readAtUtc: null },
        ],
      }
    if (name === 'SpaceTreeForMove') return { pageTree: [] }
    if (name === 'SpaceLabelDetails') return { labelDetails: page.labelDetails }
    if (name === 'CustomEmojis')
      return { customEmojis: [{ name: 'rocket', etag: '"r1"' }, { name: 'banana', etag: '"b1"' }] }
    if (name === 'GitLabStatus')
      return { gitlabStatus: { configured: true, baseUrl: 'https://gitlab.internal', viewerHasToken: true } }
    if (name === 'GitLabIssue') return { gitlabIssue: { issue: null, unavailable: { reason: 'NOT_FOUND', upstreamStatus: null } } }
    if (name === 'SearchPages')
      return {
        search: {
          // §21.13: over the whole permission-filtered hit set, not the three
          // edges below it.
          aggregateMarking: { level: 'SECRET', label: 'UK SECRET [UK/US EYES ONLY]' },
          totalCount: 12,
          pageInfo: { hasNextPage: true, endCursor: 'c10' },
          edges: [
            { cursor: 'c1', node: { snippet: '…showed a 270 ms ignition delay on the stage two vacuum engine…', headingPath: ['Stage two ignition anomaly review'], anchorId: 'stage-two-ignition-anomaly-review', page: { id: 'page-1', title: 'Stage two ignition anomaly review', spaceKey: 'PROP', marking: page.marking } } },
            { cursor: 'c2', node: { snippet: '…igniter feed line transient is visible on the unfiltered channel…', headingPath: ['Findings', 'Igniter feed'], anchorId: 'igniter-feed', page: { id: 'page-2', title: 'Telemetry review notes', spaceKey: 'PROP', marking: { level: 'OFFICIAL_SENSITIVE', levelName: 'OFFICIAL-SENSITIVE', eyesOnly: [], prefix: 'UK', label: 'UK OFFICIAL-SENSITIVE' } } } },
            { cursor: 'c3', node: { snippet: '…extended pre-press hold keeps PT-201 above the redline through ignition…', headingPath: ['Chill-in procedure', 'Pre-press'], anchorId: 'pre-press', page: { id: 'page-3', title: 'Chill-in procedure v3', spaceKey: 'PROP', marking: { level: 'OFFICIAL', levelName: 'OFFICIAL', eyesOnly: [], prefix: null, label: 'OFFICIAL' } } } },
          ],
        },
      }
    if (name === 'AskWiki')
      return {
        askWiki: {
          answer:
            'The 270 ms delay traces to turbopump inlet pressure sagging below the chill-in redline [S1]. ' +
            'The igniter feed transient was masked by the telemetry filter — the unfiltered channel confirms it [S2]. ' +
            'The corrective actions are an extended pre-press hold and an unfiltered igniter-feed channel [S1].',
          citations: [
            { pageId: 'page-1', title: 'Stage two ignition anomaly review', headingPath: ['Findings so far'], anchorId: 'findings-so-far', marking: page.marking },
            { pageId: 'page-2', title: 'Telemetry review notes', headingPath: ['Findings', 'Igniter feed'], anchorId: 'igniter-feed', marking: { level: 'OFFICIAL_SENSITIVE', levelName: 'OFFICIAL-SENSITIVE', eyesOnly: [], prefix: 'UK', label: 'UK OFFICIAL-SENSITIVE' } },
          ],
          unavailable: null,
          // §21.13's conjunctive caveat: distinct source sets are listed, so
          // the README shot shows the widest marking the app can render.
          aggregateMarking: { level: 'SECRET', label: 'UK SECRET [GB EYES ONLY] [US EYES ONLY]' },
        },
      }
    if (name === 'SearchFacets')
      return { spaces: spaces.map((s) => ({ key: s.key, name: s.name })), labels: ['anomaly', 'propulsion', 'ops'] }
    return undefined
  })
}

function shell(initialPath: string, routePath: string, element: React.ReactElement) {
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

const settle = () => new Promise((r) => setTimeout(r, 400))

// One test renders every README screen in sequence, each with a 400ms settle.
// That legitimately exceeds vitest's 5s default under full-suite load, where it
// competes with 100 other files — it then fails as a TIMEOUT with a stack
// pointing at the first line of the body, which reads like a real defect and has
// twice sent someone hunting for one. The work is genuinely this long; the
// default is what was wrong.
it('composes the README screens (writes HTML only when PREVIEW_OUT is set)', async () => {
  setEmojiRegistry([
    { name: 'rocket', etag: '"r1"' },
    { name: 'banana', etag: '"b1"' },
  ])

  const screens: { file: string; html: string }[] = []
  const capture = async (file: string, path: string, route: string, el: React.ReactElement) => {
    const r = render(shell(path, route, el))
    await settle()
    screens.push({ file, html: r.container.innerHTML })
    cleanup()
  }

  await capture('page-view.html', '/pages/page-1', 'pages/:pageId', <PageViewPage />)
  await capture('search.html', '/search?q=ignition', 'search', <SearchPage />)
  await capture('settings.html', '/settings', 'settings', <SettingsPage />)

  // The Ask page with one answered question in the transcript: prefill via ?q=,
  // fire the submit, let the mocked askWiki resolve, then capture.
  {
    const r = render(
      shell('/ask?q=Why%20did%20the%20stage%20two%20ignition%20delay%3F', 'ask', <AskWikiPage />),
    )
    await settle()
    const box = r.container.querySelector('textarea:not([aria-hidden])')
    if (box) fireEvent.change(box, { target: { value: 'Why did the stage two ignition delay?' } })
    // Enter submits (the page's own keyboard path — jsdom doesn't auto-submit
    // forms from button clicks, and this is the same route the tests use).
    if (box) fireEvent.keyDown(box, { key: 'Enter', code: 'Enter' })
    await settle()
    screens.push({ file: 'ask.html', html: r.container.innerHTML })
    cleanup()
  }

  const outDir = process.env.PREVIEW_OUT
  if (!outDir) return

  // Emotion styles land in the jsdom head; plain-CSS imports (Vite) do NOT run
  // under vitest, so embed the repo's real stylesheets explicitly — without
  // editor-content.css the emoji/table/callout styling silently vanishes.
  const cssFiles = ['../index.css', '../editor/editor-content.css'].map((rel) =>
    readFileSync(new URL(rel, import.meta.url), 'utf8'),
  )
  const styles =
    `<style>${cssFiles.join('\n')}</style>\n` +
    Array.from(document.head.querySelectorAll('style'))
      .map((s) => s.outerHTML)
      .join('\n')
  mkdirSync(outDir, { recursive: true })
  for (const s of screens) {
    writeFileSync(
      join(outDir, s.file),
      `<!doctype html><html><head><meta charset="utf-8"><title>RocketWiki</title>${styles}<style>body{margin:0;background:#fff;font-family:Roboto,Helvetica,Arial,sans-serif}</style></head><body>${s.html}</body></html>`,
    )
  }
}, 60_000)
