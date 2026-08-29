import { createBrowserRouter, Outlet } from 'react-router-dom'
import { AppShell } from './AppShell'
import { RequireAuth } from '../auth/RequireAuth'
import { RequireInstanceAdmin } from '../auth/RequireInstanceAdmin'
import { AuthCallbackPage } from '../pages/AuthCallbackPage'
import { NotFoundPage } from '../pages/NotFoundPage'

// Every route below (other than the app shell itself and the tiny guard
// components) loads via `lazy` rather than a static import. This repo ships
// to air-gapped networks (design.md §12, §15) where nobody is downloading a
// multi-megabyte bundle from a nearby CDN edge — a single bundle meant every
// route's weight (most notably TipTap for the editor, and DataGrid for the
// audit log) loaded before a user could see *any* page. `lazy` is React
// Router's own mechanism for this on a data router: it's the same
// dynamic-`import()`-driven splitting as wrapping each element in
// `React.lazy`, but the router's own navigation state (`useNavigation`,
// wired up in AppShell) covers the pending UI instead of needing a
// `<Suspense>` boundary around every route.
export const router = createBrowserRouter([
  { path: '/auth/callback', element: <AuthCallbackPage /> },
  {
    path: '/',
    element: (
      <RequireAuth>
        <AppShell />
      </RequireAuth>
    ),
    children: [
      {
        index: true,
        lazy: async () => {
          const { SpaceListPage } = await import('../pages/SpaceListPage')
          return { Component: SpaceListPage }
        },
      },
      // Creating a space has no space-admin to check against yet (it
      // doesn't exist), so this is instance-admin-only — unlike
      // rename/archive/restore on an *existing* space, which design.md
      // §6.5.1 opens to that space's own space-admin too.
      {
        path: 'spaces/new',
        lazy: async () => {
          const { CreateSpacePage } = await import('../pages/CreateSpacePage')
          return {
            Component: () => (
              <RequireInstanceAdmin>
                <CreateSpacePage />
              </RequireInstanceAdmin>
            ),
          }
        },
      },
      // Not client-gated — relies on server-side filtering (a space-admin
      // sees their own archived spaces, an instance admin sees all, design.md
      // §6.5.1), same "absent rather than forbidden" pattern as trash.
      {
        path: 'spaces/archived',
        lazy: async () => {
          const { ArchivedSpacesPage } = await import('../pages/ArchivedSpacesPage')
          return { Component: ArchivedSpacesPage }
        },
      },
      // The space's default page when it has one, its browser when it does not
      // (SpaceHomeRoute decides). A "default page" that nothing defaulted to
      // would not be a setting, so this is where §6.5.1's homepage earns its
      // name.
      {
        path: 'spaces/:spaceKey',
        lazy: async () => {
          const { SpaceHomeRoute } = await import('../pages/SpaceHomeRoute')
          return { Component: SpaceHomeRoute }
        },
      },
      // The browser did not move out of reach, it moved to a stable address.
      // Under the reserved `-` segment with every other space-level screen, so
      // it can never be shadowed by a page whose slug happens to be "browse".
      {
        path: 'spaces/:spaceKey/-/browse',
        lazy: async () => {
          const { SpaceBrowserPage } = await import('../pages/SpaceBrowserPage')
          return { Component: SpaceBrowserPage }
        },
      },
      // Space-scoped, not nested under /admin: gated by its own
      // `canManageAccess` check (instance admin OR *this* space's
      // space-admin), so a space-admin who isn't an instance admin can
      // still reach it (design.md §6.5).
      // Per-space management in one place (design.md §6.5.1). Not gated by a
      // route guard: the page itself decides what to offer from the same
      // server-filtered `grants` signal the space browser uses, and a reader
      // who lands here sees the space's details read-only rather than a
      // forbidden screen.
      {
        path: 'spaces/:spaceKey/-/admin',
        lazy: async () => {
          const { SpaceSettingsPage } = await import('../pages/SpaceSettingsPage')
          return { Component: SpaceSettingsPage }
        },
      },
      // The readable page address: slugs are unique per space and the hierarchy is
      // deliberately absent, so moving a page never changes its URL. /pages/{id}
      // still works and is what anything holding only an id links to.
      //
      // Every system page for a space lives under a `-` segment
      // (/spaces/{key}/-/admin) so this dynamic route can never collide with one.
      // That leaves exactly ONE reserved slug, `-`, instead of a list that had to
      // grow every time a space route was added — and where forgetting to grow it
      // would silently shadow every page already using that word.
      {
        path: 'spaces/:spaceKey/:slug',
        lazy: async () => {
          const { SlugPageRoute } = await import('../pages/SlugPageRoute')
          return { Component: SlugPageRoute }
        },
      },
      // Not router-gated: the server returns null to anyone who administers
      // neither the instance nor this space, and to a key that does not resolve —
      // the same answer for both (§6.7). A guard here would duplicate a decision
      // the server has already made and is the only one able to enforce.
      {
        path: 'spaces/:spaceKey/-/analytics',
        lazy: async () => {
          const { AnalyticsPage } = await import('../pages/AnalyticsPage')
          return { Component: AnalyticsPage }
        },
      },
      {
        path: 'spaces/:spaceKey/-/grants',
        lazy: async () => {
          const { SpaceGrantsPage } = await import('../pages/SpaceGrantsPage')
          return { Component: SpaceGrantsPage }
        },
      },
      // Not client-gated — the trash query is permission-filtered
      // server-side, same as the read paths generally (design.md §6.7).
      {
        path: 'spaces/:spaceKey/-/trash',
        lazy: async () => {
          const { TrashPage } = await import('../pages/TrashPage')
          return { Component: TrashPage }
        },
      },
      {
        path: 'spaces/:spaceKey/-/import-report',
        lazy: async () => {
          const { ImportReportPage } = await import('../pages/ImportReportPage')
          return { Component: ImportReportPage }
        },
      },
      {
        path: 'pages/:pageId',
        lazy: async () => {
          const { PageViewPage } = await import('../pages/PageViewPage')
          return { Component: PageViewPage }
        },
      },
      {
        path: 'pages/:pageId/edit',
        lazy: async () => {
          const { PageEditPage } = await import('../pages/PageEditPage')
          return { Component: PageEditPage }
        },
      },
      // Everything about a page that is not the page. Self-gated on the
      // server-computed `canEdit` that arrives with the page rather than by a
      // router guard, so there is no second round trip and no second copy of the
      // rule — the screen itself says who it is for.
      {
        path: 'pages/:pageId/details',
        lazy: async () => {
          const { PageDetailsPage } = await import('../pages/PageDetailsPage')
          return { Component: PageDetailsPage }
        },
      },
      // The address properties used to live at. Kept as a redirect rather than
      // deleted: it is the sort of URL someone pastes into a ticket, and a link
      // that used to work should not start 404ing because a screen grew.
      {
        path: 'pages/:pageId/properties',
        lazy: async () => {
          const { PropertiesRedirect } = await import('../pages/PageDetailsPage')
          return { Component: PropertiesRedirect }
        },
      },
      // Also self-gated (page.canManageAccess) rather than router-level —
      // same reasoning as space grants above.
      {
        path: 'pages/:pageId/permissions',
        lazy: async () => {
          const { PagePermissionsPage } = await import('../pages/PagePermissionsPage')
          return { Component: PagePermissionsPage }
        },
      },
      // In-app help. Under the reserved `-` segment like every other system
      // screen, so it can never be shadowed by a future top-level route or
      // shadow one. Ungated: the reader most likely to need it is the one who
      // holds a role in nothing yet.
      {
        path: '-/docs',
        lazy: async () => {
          const { HelpPage } = await import('../pages/HelpPage')
          return { Component: HelpPage }
        },
      },
      {
        path: '-/docs/:topic',
        lazy: async () => {
          const { HelpPage } = await import('../pages/HelpPage')
          return { Component: HelpPage }
        },
      },
      {
        path: 'search',
        lazy: async () => {
          const { SearchPage } = await import('../pages/SearchPage')
          return { Component: SearchPage }
        },
      },
      // Not router-gated: NOT_CONFIGURED is a payload fact learned by
      // asking (design.md §9.5 — no status query exists), so the page
      // itself renders the feature-absent state (askAvailability.ts) while
      // the shell/search affordances collapse.
      {
        path: 'ask',
        lazy: async () => {
          const { AskWikiPage } = await import('../pages/AskWikiPage')
          return { Component: AskWikiPage }
        },
      },
      // Per-user settings (design.md §18's GitLab token surface). Reachable
      // by anyone; sections gate themselves on server-reported state
      // (gitlabStatus.configured) rather than router-level guards.
      {
        path: 'settings',
        lazy: async () => {
          const { SettingsPage } = await import('../pages/SettingsPage')
          return { Component: SettingsPage }
        },
      },
      {
        path: 'admin',
        element: (
          <RequireInstanceAdmin>
            <Outlet />
          </RequireInstanceAdmin>
        ),
        children: [
          {
            index: true,
            lazy: async () => {
              const { AdminPage } = await import('../pages/AdminPage')
              return { Component: AdminPage }
            },
          },
          // Site-wide: the same screen with no space key. Inside the
          // RequireInstanceAdmin block like its neighbours, though the server
          // gates it regardless.
          {
            path: 'analytics',
            lazy: async () => {
              const { AnalyticsPage } = await import('../pages/AnalyticsPage')
              return { Component: AnalyticsPage }
            },
          },
          {
            path: 'audit',
            lazy: async () => {
              const { AuditLogPage } = await import('../pages/AuditLogPage')
              return { Component: AuditLogPage }
            },
          },
          {
            path: 'sync',
            lazy: async () => {
              const { SyncStatusPage } = await import('../pages/SyncStatusPage')
              return { Component: SyncStatusPage }
            },
          },
          {
            path: 'emojis',
            lazy: async () => {
              const { AdminEmojisPage } = await import('../pages/AdminEmojisPage')
              return { Component: AdminEmojisPage }
            },
          },
          // The page-property key registry (design.md §20.1) — instance
          // vocabulary, same shape as the emoji registry beside it.
          {
            path: 'property-keys',
            lazy: async () => {
              const { AdminPropertyKeysPage } = await import('../pages/AdminPropertyKeysPage')
              return { Component: AdminPropertyKeysPage }
            },
          },
        ],
      },
      // Statically imported, not `lazy`: AccessGate.tsx renders it directly
      // for the "absent rather than forbidden" pattern (design.md §6.7), so
      // it's already pulled into the shared bundle by every guarded route —
      // lazy-loading it here would just add an unmet-splitting warning
      // without saving anything.
      { path: '*', element: <NotFoundPage /> },
    ],
  },
])
