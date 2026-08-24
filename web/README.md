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

## Vite template notes

Two official React plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

The React Compiler is not enabled because of its impact on dev & build
performance. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).
