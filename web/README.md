# RocketWiki web (SPA)

Vite + React + TypeScript (strict), MUI, TipTap, urql with a generated
GraphQL client (`npm run codegen` — nothing compiles without it). The
design source of truth is `../design.md`; §4 (round-trip rule), §6
(permissions shape the UI), and §8 (API) matter most here.

## Typechecking: use `npm run typecheck`, never `tsc --noEmit`

`tsconfig.json` is solution-style — `files: []` plus project references — so
**`npx tsc --noEmit` type-checks exactly zero files here and exits 0 no matter
what is broken.** It looks like a passing typecheck and is not one. Only the
build-mode form (`tsc -b`) follows the references.

This is not hypothetical: it hid 23 real errors, including MUI v9 dropping the
`Stack`/`Typography` system props, so `alignItems`, `flexWrap` and
`whiteSpace` were silently ignored on five screens that genuinely were not
laying out as written. CI (`npm run build`) caught it; a local `--noEmit` never
would.

```
npm run typecheck   # tsc -b --force — the real one
```

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
never "No spaces.". Where the consequence depends on who is looking, the
branch lives in `unavailableCopy.ts` (`describeNoSpaces`) rather than at each
surface: the rail and the space list drifted apart once, and the rail ended up
telling readers to press a button that is hidden from them.

An **action that failed** uses `severity="warning"` and is dismissible
(`onClose`). `severity="error"` is for the genuinely exceptional — a failed
sign-in, an upload whose file is now nowhere. Two screens used `error` for an
ordinary refused mutation and gave no way to clear it.

A **destructive confirmation** is `feedback/ConfirmDialog.tsx`: the safe option
takes focus, the destructive one is filled. Do not hand-roll one — the three
that existed disagreed about both, and the quietest button on the screen was
the irreversible one. Dialogs carrying a **form** go full-screen below `sm`
(`app/useDialogFullScreen.ts`); confirmations stay centred.

Every screen's `<h1>` comes from `app/PageHeader.tsx` — the screen's name as
the heading, the subject on the line below as a link back to it. Nine screens
had grown five spellings of that idea and four had no way back at all. The
heading also names the browser tab, via `app/documentTitle.ts`.

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
is no top app bar, which is what the template does at desktop widths. Below
both, when the screen shows marked content, the classification banner
(`src/markings/ClassificationBanner.tsx`) is the frame's last row — a layout
row rather than a fixed overlay, so the scroll region ends above it and nothing
is ever underneath it. Printed, the frame gives up its fixed height and the
content region its scrolling, so a page prints in full with the marking at the
head and the foot of the printed document; the rail, the header's controls and
every screen's action buttons stay off paper (`src/theme/print.ts` says what
prints and what does not, and `a11y/print.spec.ts` measures it).

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
