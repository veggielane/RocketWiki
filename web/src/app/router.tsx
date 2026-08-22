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
      {
        path: 'spaces/:spaceKey',
        lazy: async () => {
          const { SpaceBrowserPage } = await import('../pages/SpaceBrowserPage')
          return { Component: SpaceBrowserPage }
        },
      },
      // Space-scoped, not nested under /admin: gated by its own
      // `canManageAccess` check (instance admin OR *this* space's
      // space-admin), so a space-admin who isn't an instance admin can
      // still reach it (design.md §6.5).
      {
        path: 'spaces/:spaceKey/grants',
        lazy: async () => {
          const { SpaceGrantsPage } = await import('../pages/SpaceGrantsPage')
          return { Component: SpaceGrantsPage }
        },
      },
      // Not client-gated — the trash query is permission-filtered
      // server-side, same as the read paths generally (design.md §6.7).
      {
        path: 'spaces/:spaceKey/trash',
        lazy: async () => {
          const { TrashPage } = await import('../pages/TrashPage')
          return { Component: TrashPage }
        },
      },
      {
        path: 'spaces/:spaceKey/import-report',
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
      // Also self-gated (page.canManageAccess) rather than router-level —
      // same reasoning as space grants above.
      {
        path: 'pages/:pageId/permissions',
        lazy: async () => {
          const { PagePermissionsPage } = await import('../pages/PagePermissionsPage')
          return { Component: PagePermissionsPage }
        },
      },
      {
        path: 'search',
        lazy: async () => {
          const { SearchPage } = await import('../pages/SearchPage')
          return { Component: SearchPage }
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
          {
            path: 'audit',
            lazy: async () => {
              const { AuditLogPage } = await import('../pages/AuditLogPage')
              return { Component: AuditLogPage }
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
