# RocketWiki web (SPA)

Vite + React + TypeScript (strict), MUI, TipTap, urql with a generated
GraphQL client (`npm run codegen` — nothing compiles without it). The
design source of truth is `../design.md`; §4 (round-trip rule), §6
(permissions shape the UI), and §8 (API) matter most here.

## UI feedback conventions

One rule decides which surface a message uses — pick by what the user must
do with it, not by severity:

- **Snackbar** — transient success or FYI that requires no action
  ("Saved revision 12", "Autosaved"). Always auto-hides after the shared
  `SNACKBAR_AUTO_HIDE_MS` (`src/feedback/snackbar.ts`); never hard-code a
  duration.
- **Inline `Alert`** — state the user should know about and may need to act
  on, tied to the place it applies (the replica banner, a failed download,
  an unreachable integration). It stays until the state changes or the user
  dismisses it.
- **Dialog** — a typed error demanding a choice before work can continue:
  `StaleRevisionError` opens the merge flow, `ReadOnlyReplicaError` opens
  the replica explainer. Designed failure modes are UX, never raw error
  toasts.

Degradation copy ("this can't work right now") lives in
`src/feedback/unavailableCopy.ts` — one module, shared voice, per-surface
reasons. Add new reasons there rather than writing one-off strings; keep a
surface's reasons distinct instead of flattening them into a generic
failure. design.md §12's word for a synced space is **replica** — no
user-facing surface says "mirror"/"mirrored".

Empty states say the fact **and** the consequence:
"No spaces yet — create one to start writing", not "No spaces.".
An empty state and a *failed load* are different things and must not borrow
each other's words — a query that errored says `describeLoadFailure(...)`,
never "No spaces.".

## Look and feel

The design is MUI's Dashboard template (v9.4.0, the version this repo runs) —
its layout and its elements, written in this repo's own structure rather than
vendored from it. The **elements** are the theme: colour ramps in
`src/theme/palette.ts`, component overrides in
`src/theme/componentCustomizations.ts`, both of which list what was
deliberately not adopted and why. The **layout** is the shell: a navigation
rail (`src/app/SideMenu.tsx`) with identity at the top and the signed-in user
at the bottom, and a content region whose own header strip
(`src/app/AppHeader.tsx`) carries the breadcrumb and the global actions. There
is no top app bar, which is what the template does at desktop widths.

None of the template's dashboard content is here — no KPI cards, no charts,
no sample grid — and none of its four `@mui/x-*` dependencies.

Build from MUI components and this theme. A one-off `sx` for a genuinely
one-off layout is fine; a one-off colour, radius or spacing value is a token
that belongs in the theme. New palette values are not free — the browser a11y
tier (`docs/ACCESSIBILITY.md`) measures contrast on real pixels in **both**
themes, and a colour that reads well in light mode routinely fails in dark.

## Where a file goes

`src/pages/` holds **route components only** — the things `app/router.tsx`
mounts. Everything else lives in the folder that owns its domain, beside
the logic or copy module it depends on: the move dialog with
`access/move/visibilityChange.ts`, the delete dialog with
`trash/describeDeleteOutcome.ts`, the merge-flow dialog with
`diff/staleDiff.ts`, the replica explainer with the replica vocabulary in
`feedback/`. There is no `components/`, `dialogs/` or `hooks/` bucket
anywhere in `src/`, and adding one would give every future file two
plausible homes; a hook likewise goes in its owner's folder as
`useThing.ts` (`presence/usePresence.ts`, `auth/useIsInstanceAdmin.ts`).
Tests sit in that same folder's `__tests__/`, except for cross-cutting
policy suites — `test/dialogsA11y.test.tsx` enforces "every dialog passes
axe in its open state" across features, so it lives with the axe policy it
applies rather than in any one feature.

## Vite template notes

Two official React plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

The React Compiler is not enabled because of its impact on dev & build
performance. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).
