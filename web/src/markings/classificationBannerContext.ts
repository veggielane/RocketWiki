import { createContext, useContext, useLayoutEffect } from 'react'
import type { ClassificationLevel } from '../graphql/generated/graphql'

/** What a screen declares about the marking of the thing it is showing. */
export interface ClassificationBannerMarking {
  /** The server-built display string, verbatim (ClassificationBanner.tsx). */
  label: string
  /** Styling only — the meaning is all in `label`. */
  level: ClassificationLevel
  /** What the marking is about, for screen readers: "this page", "this answer". */
  scopeLabel: string
}

export type SetClassificationBanner = (marking: ClassificationBannerMarking | null) => void

/**
 * How a screen tells the shell what its content is classified as.
 *
 * The banner is a row of the shell's layout (AppShell.tsx), so the shell has to
 * be the one that renders it — but only the screen knows the marking, which
 * arrives with the page. Same shape as PageTitleContext: the screen registers,
 * the shell renders exactly once. Defaulted to a no-op so a screen rendered
 * outside the shell — every page-level test does this — neither crashes nor
 * needs a stand-in banner.
 */
export const ClassificationBannerContext = createContext<SetClassificationBanner>(() => {})

/**
 * Declares the protective marking of what this screen is showing; the shell
 * renders it as the classification banner for as long as the screen is up.
 *
 * Pass `null` while the content is still loading, or when there is nothing
 * marked on screen — the banner is absent then, which is the truth. Never a
 * guess and never the previous page's marking: a stale marking on a screen is
 * a wrong marking, not a harmless one.
 *
 * A layout effect rather than a passive one, on both edges. Registering after
 * paint would put one painted frame of marked content on screen with no
 * banner; clearing after paint would leave the departed page's marking on the
 * screen that replaced it for a frame, and a screenshot has no way to know it
 * caught that frame. Layout-effect state updates are flushed before the
 * browser paints, so the content and its banner arrive together and leave
 * together.
 */
export function useClassificationBanner(marking: ClassificationBannerMarking | null): void {
  const setBanner = useContext(ClassificationBannerContext)
  const label = marking?.label
  const level = marking?.level
  const scopeLabel = marking?.scopeLabel
  useLayoutEffect(() => {
    // Rebuilt from the fields, so the dependency list is the three strings a
    // screen would actually change rather than an object it makes fresh each
    // render — which would register on every render, including the ones this
    // registration causes.
    setBanner(label !== undefined && level !== undefined && scopeLabel !== undefined ? { label, level, scopeLabel } : null)
    // Cleared on unmount so the next screen does not inherit this one's
    // marking during the gap before its own content resolves.
    return () => setBanner(null)
  }, [setBanner, label, level, scopeLabel])
}
