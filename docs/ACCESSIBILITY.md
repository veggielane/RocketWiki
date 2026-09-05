# Accessibility

**Target: WCAG 2.2 Level AA** for the RocketWiki SPA (`web/`).

Honest before helpful, as everywhere in this repo: automated accessibility
tooling can decide roughly a third to half of the WCAG 2.2 AA success
criteria. The rest — keyboard operability, focus order, meaningful
sequence, error-recovery UX — is addressed by design patterns enforced in
component tests, plus a periodic manual audit whose scope and current
status are listed below. A green CI run means "no violation any automated
check can detect," not "conformant"; the gap between those two statements
is exactly what this document is for.

## The two automated layers

### 1. Component layer — jsdom axe, every `npm test`, every CI run

Every page-level test file (`web/src/pages/__tests__/`), the major
composites (notification popover open, editor toolbar with table controls
and the emoji picker open, the rule builder, the ask page, every dialog in
its open state), and a whole-screen sweep in
`web/src/preview/a11yScreens.test.tsx` call one shared helper:

```ts
import { expectNoAxeViolations } from '../test/axe'
await expectNoAxeViolations() // defaults to document.body — portals included
```

`web/src/test/axe.ts` runs `axe-core` (pinned, direct dependency) with the
WCAG 2.2 AA tag set: `wcag2a, wcag2aa, wcag21a, wcag21aa, wcag22aa`.
It replaced the earlier `vitest-axe` matcher: vitest-axe 0.1.0's
`toHaveNoViolations` type augmentation targets the pre-Vitest-4 assertion
interface, so the matcher registered but never type-checked. A plain
helper fixes that at the root (no matcher, no module augmentation to chase
across Vitest majors), gives one canonical home for the tag set and the
exclusions below, and prints offending rules/nodes on failure.

**Rules excluded in this layer only** (jsdom does not lay out or paint, so
these rules cannot produce trustworthy answers there — the browser layer
owns every one of them):

| Rule | WCAG | Why it cannot run in jsdom |
|---|---|---|
| `color-contrast` | 1.4.3 | Contrast needs painted pixels and resolved computed colors; jsdom has neither |
| `target-size` | 2.5.8 | Measures bounding boxes; every jsdom rect is 0×0 |
| `link-in-text-block` | 1.4.1 | Detects links distinguished only by color, which needs computed colors |

These are the **only** rule suppressions anywhere in the project, and they
are exclusions-with-an-owner, not waivers. The browser layer runs the full
tag set with **no** disabled rules.

### 2. Browser layer — Playwright + `@axe-core/playwright`, real Chromium (CI job `a11y`)

`web/src/preview/a11yScreens.test.tsx` renders real components with staged
data (same mock seams as the test suite) into one self-contained HTML file
per screen **per theme** — dark-mode contrast is where audits usually
bleed, so every screen exists as `--light` and `--dark`, with the
`data-theme` attribute stamped exactly as `ColorModeProvider` stamps it in
the live app. Captured screens (23 × 2 themes):

page view (with presence viewers), page edit (full editor + formatting
toolbar), search, settings, ask (answered, citations + sources), admin
emojis, admin property keys (create form + DataGrid), page properties
(editable table of text fields + key picker), page permissions, space
browser (tree + lock badge + replica banner), trash, audit log (DataGrid),
notification popover **open**, move dialog **open with the
visibility-change warning**, stale-revision dialog **with the diff**, and marking levels (all four classification banners and
badges in one render — the page-view and properties screens can each only
stage one marking, so the rungs that are not staged there would otherwise
never be contrast-checked), a page-list widget — the only screen where
MUI marking components render inside editor-content's plain-CSS surface, so
the only place those two colour systems are checked against each other —
plus page history, analytics, help, the admin index (its `Planned`
treatment for unbuilt sections) and the **space list**, which is the first
screen every user sees and the only one rendering the replica chip inside a
card.

`web/a11y/` (its own package, so Playwright and its browser downloads stay
out of the SPA's dependency tree) loads each file in Chromium and runs axe
with the full WCAG 2.2 AA tag set — `color-contrast` and `target-size`
enforced against real layout. Any violation fails the `a11y` CI job; the
JSON + HTML report and the capture set upload as an artifact on failure.
The spec also fails if the capture set is missing or lost a theme variant —
a green run over zero files would be a tier that doesn't exist.

**Two Playwright projects, not one.** `chromium` runs every capture with the
browser reporting `prefers-color-scheme: light` (Chromium's default);
`os-dark` re-runs the **light** captures with it reporting dark. That second
project exists for one combination the first cannot produce: an app rendering
in light mode inside a dark-preference browser. `editor-content.css` themes on
the media query *as well as* on `data-theme`, and because captures stamp
`data-theme` while Chromium defaults to light, that pair was invisible here —
it shipped with light chrome around a page body still wearing dark code
blocks, dark table headers and a `#6b9bf7` link on white (~2.6:1). The fix is a
`:root[data-theme='light']` reset in the stylesheet; this project is what
keeps it honest. Running the dark captures under a dark OS would only re-test
agreement, so `a11y.spec.ts` skips them there.

### Running both layers locally

```bash
cd web
npm ci && npm run codegen
npm test                       # component layer (jsdom axe passes included)

# browser layer
PREVIEW_OUT="$PWD/a11y/screens" npx vitest run src/preview/a11yScreens.test.tsx
cd a11y
npm ci
npx playwright install chromium   # first time only
npx playwright test
```

## Where automated checking abstains (and what covers the gap)

axe reports "incomplete" (abstains) where it cannot compute an answer.
These abstentions were investigated rather than ignored; several app
changes exist purely to shrink them:

- **Dark elevation overlays**: MUI's dark-mode elevation gradient made all
  app-bar/drawer text uncheckable. The theme now flattens Paper
  (`backgroundImage: 'none'`), which matches the app's flat look and puts
  those surfaces back under automated contrast checking.
- **Co-edit caret labels**: the captures stage no live edit session, so the
  name label beside a remote caret is never under axe. Caret **label**
  contrast is guaranteed by construction instead:
  `web/src/presence/readableTextOn.ts` picks black or white per
  server-assigned colour (mathematically ≥ √21 ≈ 4.58:1 for any
  background), with exhaustive tests sweeping all 360 generator hues and a
  500-sample RGB sweep (`readableTextOn.test.ts`).
- **MUI outlined inputs**: the notched-outline `<fieldset>` overlays the
  input box, so axe abstains on input text. The color pair involved
  (`text.primary` on `background.paper`) is the same pair verified on
  ordinary text throughout every capture.
- **Content behind open dialogs/popovers**: abstained (and `aria-hidden`
  by MUI's modal) in the dialog captures; the same content is checked in
  its own non-modal captures.

## Manual-audit criteria and current status

Automated coverage above; the following are human-verified. "Slice
covered" cites an existing behavior test holding that slice in place.

| Criterion | Status |
|---|---|
| 2.1.1/2.1.2 Keyboard, no traps | Slices covered by tests: emoji autocomplete (`emojiSuggestion.test.ts`: Arrow/Enter/Tab/Escape, Escape-then-fall-through), ask composer (`askWikiPage.test.tsx`: Enter submits, Shift+Enter newline), table editing keymaps (`tableEditing.test.ts`), dialog flows (`StaleRevisionDialog.test.tsx`). MUI dialogs/menus provide focus trapping + Escape. Full-app keyboard walk: manual, periodic. |
| 2.4.1 Bypass blocks | Skip link in `AppShell` ("Skip to main content") + `main`/`nav` landmarks; axe `bypass` checks presence, a human checks usefulness. |
| 2.4.3 Focus order | Manual. DOM order matches visual order by construction (no CSS reordering); spot-checked. |
| 2.4.7/2.4.13 Focus visible/appearance | MUI focus rings kept throughout; the custom task-list checkbox draws its own `:focus-visible` outline. Manual spot-check per release. |
| 2.4.11 Focus not obscured | Nothing is fixed over the top of the scroll container — the header strip scrolls with the content — but the classification banner is fixed across its bottom, so `scroll-padding-bottom` on `main` keeps keyboard-focus targets from landing beneath it, and the drawer reserves the same strip for its account block. Manual verification on long pages. |
| 2.5.7 Dragging movements | No drag-only operation in the wiki's own UI: table column resize is disabled (`resizable: false`), image drag-drop upload has the attachment-upload button as the non-drag path. Judged met; re-check when adding drag affordances (tree reordering!). |
| 3.2.6 Consistent help | No help mechanism exists in the SPA, so there is nothing to be inconsistently located. Vacuously met; revisit if a help affordance ships. |
| 3.3.7 Redundant entry | No multi-step flows re-request information (auth is OIDC redirect; forms are single-step). Judged met. |
| 1.4.4/1.4.10 Resize / reflow | Manual: 200% zoom and 320 px-wide checks per release. |
| 4.1.3 Status messages | Ask page uses `role="status"` for the pending state; snackbars are MUI `Alert`s. Partial — audit other async outcomes manually. |

## Known limitations

- **draw.io diagrams**: the embedded editor is diagrams.net inside an
  iframe; its INTERNAL accessibility belongs to that project. RocketWiki's
  obligation is the embed affordances (open/edit buttons, labels), which
  the component layer checks.
- **Mermaid diagrams** render with mermaid's `neutral` (light) theme in
  both app themes, and SVG text contrast inside diagrams is not covered by
  either automated layer (the jsdom captures cannot execute mermaid).
  Light-on-light is acceptable; dark-mode users see a light diagram panel —
  legible, but visibly unthemed. Tracked as a wanted improvement.
- **MUI X DataGrid internals** (audit viewer): the grid's own ARIA comes
  from MUI X. The captures check what it renders in jsdom (grid chrome,
  toolbar, rows); grid virtualization behavior under keyboard is trusted
  upstream + manual.
- The captures are static snapshots: states not staged (error snackbars on
  live mutations, upload progress) rely on the component layer and manual
  passes.

## Notable fixes behind the current green (for archaeology)

- Palette: light `warning.main` → `#b45309` (default `#ed6c02` failed
  1.4.3 as text and under white contained-button text); dark
  `error.contrastText` → black (white-on-`#f44336` was 3.7:1 — the DENIED
  audit chip). Rationale comments live in `web/src/theme/palette.ts`,
  alongside the two ramp values the MUI Dashboard template supplies that
  this app cannot use as-is: its `primary` (4.14:1 on white in light mode,
  4.62:1 on the dark paper) and its `warning`, whose orange is 2.8:1 under
  white contained-button text — the same failure `#ed6c02` was replaced for.
- `ColorModeProvider` now stamps `data-theme` on the root element — the
  editor's plain-CSS theming keyed on it but nothing ever set it, so
  app-toggled dark mode kept light code-block/table-header backgrounds
  under white text. Found by the dark captures.
- Task-list checkboxes: native 13×13 checkbox failed 2.5.8; now a 24×24
  target with a custom-drawn 16×16 box and a restored focus ring
  (`editor-content.css`).
- Notification bell: `Menu` (role `menu`) wrapping a `<ul>` of `<div>`s
  violated `aria-required-children`/`list`; now a `Popover` with a real
  `List`/`ListItem` tree. Read-state de-emphasis via color token, not
  `opacity` (which multiplies below the contrast floor).
- `ul > a` (missing `<li>`) fixed in search results, ask sources, and the
  space browser's label-match list.
- The emoji `:` autocomplete implements the combobox pattern: the editor's
  `role="textbox"` DOM carries `aria-controls` + `aria-activedescendant`
  pointing at real option ids while the popup is open
  (`EmojiSuggestionPopup.tsx`, tested in `emojiSuggestionPopup.test.tsx`).
- `UserAvatar` roots are `role="img"` with the display name (MUI Tooltip
  injects `aria-label` onto its child, which a generic div may not carry),
  and initials text adapts black/white to the assigned colour.
