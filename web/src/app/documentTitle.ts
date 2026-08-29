import { createContext, useContext, useEffect } from 'react'

/** Suffixed onto every title, so a tab is identifiable when the label is truncated. */
export const APP_NAME = 'RocketWiki'

export type SetPageTitle = (title: string | null) => void

/**
 * How a screen tells the shell what it is looking at.
 *
 * A context with ONE writer rather than each screen setting `document.title`
 * itself, because both would want to: the shell knows the route
 * (app/routeCrumbs.ts can name `/search` without asking anyone) and only the
 * screen knows the subject (`/pages/{id}` is a title the shell would have to
 * guess at from a slug). Child effects run before parent effects, so two
 * independent writers would have the shell's route-derived fallback overwrite
 * the screen's real answer on every navigation — the screen would win on
 * re-render and lose on arrival, which is the sort of bug that only shows up as
 * "the tab title flickers".
 *
 * So: screens register a title, the shell composes and writes exactly once
 * (AppShell.tsx). Defaulted to a no-op so a screen rendered outside the shell
 * — every page-level test does this — neither crashes nor scribbles on the
 * jsdom document.
 */
export const PageTitleContext = createContext<SetPageTitle>(() => {})

/**
 * Name this screen for the browser tab, the history menu and a bookmark.
 *
 * Pass the SUBJECT where a screen has one ("Stage two ignition anomaly"), not
 * the screen's function — the shell already knows it is on the details route
 * and will say so. Pass `null`/`undefined` while the subject is still loading
 * and the route's own name stands in until it arrives.
 */
export function useDocumentTitle(title: string | null | undefined): void {
  const setPageTitle = useContext(PageTitleContext)
  useEffect(() => {
    setPageTitle(title ?? null)
    // Cleared on unmount so the next route does not inherit the last screen's
    // subject during the gap before its own query resolves.
    return () => setPageTitle(null)
  }, [setPageTitle, title])
}

/** `Subject — RocketWiki`, or just the app name when there is no subject yet. */
export function composeDocumentTitle(subject: string | null | undefined): string {
  const trimmed = subject?.trim()
  return trimmed ? `${trimmed} — ${APP_NAME}` : APP_NAME
}
