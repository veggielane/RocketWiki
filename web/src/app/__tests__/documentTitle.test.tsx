import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { useState } from 'react'
import { APP_NAME, PageTitleContext, composeDocumentTitle, useDocumentTitle } from '../documentTitle'

/**
 * Every route in the app was "RocketWiki" — in the browser tab, in the history
 * menu, and in a bookmark. `document.title` was assigned nowhere in `src/`.
 * That is WCAG 2.4.2 (Page Titled) on an SPA, and it is also what makes a
 * wiki's many-open-tabs workflow usable at all.
 */

/** The shell's half of the arrangement: one writer, fed by whatever a screen registered. */
function Shell({ routeFallback, children }: { routeFallback: string; children: React.ReactNode }) {
  const [pageTitle, setPageTitle] = useState<string | null>(null)
  return (
    <>
      <PageTitleContext value={setPageTitle}>{children}</PageTitleContext>
      <span data-testid="title">{composeDocumentTitle(pageTitle ?? routeFallback)}</span>
    </>
  )
}

function Screen({ subject }: { subject: string | null }) {
  useDocumentTitle(subject)
  return <div>screen</div>
}

describe('composeDocumentTitle', () => {
  it('suffixes the app name so a truncated tab is still identifiable', () => {
    expect(composeDocumentTitle('Audit log')).toBe(`Audit log — ${APP_NAME}`)
  })

  it('falls back to the app name alone rather than rendering a dangling dash', () => {
    expect(composeDocumentTitle(null)).toBe(APP_NAME)
    expect(composeDocumentTitle('   ')).toBe(APP_NAME)
  })
})

describe('useDocumentTitle', () => {
  it("uses the screen's subject when it has one", () => {
    render(
      <Shell routeFallback="Spaces">
        <Screen subject="Stage two ignition anomaly" />
      </Shell>,
    )
    expect(screen.getByTestId('title')).toHaveTextContent(`Stage two ignition anomaly — ${APP_NAME}`)
  })

  it("falls back to the route's own name while the subject is still loading", () => {
    // A page's title arrives with its query; the route knows what it is called
    // immediately. The tab should not sit on the bare app name in between.
    render(
      <Shell routeFallback="Search">
        <Screen subject={null} />
      </Shell>,
    )
    expect(screen.getByTestId('title')).toHaveTextContent(`Search — ${APP_NAME}`)
  })

  it('releases the subject when the screen unmounts', () => {
    const { rerender } = render(
      <Shell routeFallback="Spaces">
        <Screen subject="Runbook" />
      </Shell>,
    )
    expect(screen.getByTestId('title')).toHaveTextContent('Runbook')

    // Otherwise the next route would wear the last screen's subject during the
    // gap before its own query resolves.
    rerender(<Shell routeFallback="Spaces">{null}</Shell>)
    expect(screen.getByTestId('title')).toHaveTextContent(`Spaces — ${APP_NAME}`)
  })

  it('is inert outside the shell, so a page-level test neither crashes nor scribbles', () => {
    // Every page test renders its component without the shell around it.
    expect(() => render(<Screen subject="Anything" />)).not.toThrow()
  })
})
