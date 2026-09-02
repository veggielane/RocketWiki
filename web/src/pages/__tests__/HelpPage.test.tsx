import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { HelpPage } from '../HelpPage'
import { HELP_SECTIONS, HELP_TOPICS } from '../../help/topics'
import { expectNoAxeViolations } from '../../test/axe'

function renderHelp(path = '/-/docs') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/-/docs" element={<HelpPage />} />
        <Route path="/-/docs/:topic" element={<HelpPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('HelpPage', () => {
  it('needs no server at all', async () => {
    // Deliberately rendered with no urql provider. Help has to work for the
    // reader most likely to need it — someone new, who may hold a role in
    // nothing — so it must not depend on a query that could come back empty.
    renderHelp()
    expect(await screen.findByRole('heading', { name: 'Help' })).toBeInTheDocument()
  })

  it('shows the first topic when none is named', async () => {
    renderHelp()
    expect(await screen.findByRole('link', { name: /Getting started/ })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Getting started' })).toBeInTheDocument()
  })

  it('renders the topic in the route', async () => {
    renderHelp('/-/docs/classification')
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Classifications and markings' }),
    ).toBeInTheDocument()
  })

  it('offers both audiences, and offers admin topics to everyone', async () => {
    // Not gated on isInstanceAdmin: these describe how grants and markings WORK
    // rather than who holds what, and gating would mostly hide them from the
    // space admin who needed them.
    renderHelp()
    expect(await screen.findByRole('heading', { name: 'Using RocketWiki' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Administering RocketWiki' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Administering the instance/ })).toBeInTheDocument()
  })

  it('says so for a topic that does not exist, rather than showing a blank page', async () => {
    renderHelp('/-/docs/no-such-topic')
    expect(await screen.findByText(/No help topic called/)).toBeInTheDocument()
  })

  it('has no axe violations', async () => {
    renderHelp()
    await screen.findByRole('heading', { level: 1, name: 'Getting started' })
    await expectNoAxeViolations()
  })
})

describe('help content', () => {
  it('gives every topic a title, a summary and a body', () => {
    // The list renders all three, so an empty one is a gap in the UI rather than
    // just a gap in the docs.
    for (const topic of HELP_TOPICS) {
      expect(topic.title.length, topic.slug).toBeGreaterThan(0)
      expect(topic.summary.length, topic.slug).toBeGreaterThan(0)
      expect(topic.markdown.trim().length, topic.slug).toBeGreaterThan(200)
    }
  })

  it('starts every topic with an h1, since the page renders no title of its own', () => {
    for (const topic of HELP_TOPICS) {
      expect(topic.markdown.trimStart().startsWith('# '), topic.slug).toBe(true)
    }
  })

  it('has unique slugs', () => {
    const slugs = HELP_TOPICS.map((t) => t.slug)
    expect(new Set(slugs).size).toBe(slugs.length)
  })

  it('keeps every section non-empty', () => {
    for (const section of HELP_SECTIONS) {
      expect(section.topics.length, section.title).toBeGreaterThan(0)
    }
  })

  it('lists every content file in the manifest', () => {
    // The docs site generates one User guide page per manifest topic; a
    // content/*.md the manifest forgets is silently missing from both the app
    // and the site. This is the SPA-side half of the generator's orphan check.
    const files = import.meta.glob('../../help/content/*.md', {
      query: '?raw',
      import: 'default',
      eager: true,
    }) as Record<string, string>
    const slugs = new Set(HELP_TOPICS.map((t) => t.slug))
    for (const filePath of Object.keys(files)) {
      const slug = filePath.replace(/^.*\//, '').replace(/\.md$/, '')
      expect(slugs.has(slug), `${filePath} is not listed in manifest.json`).toBe(true)
    }
  })
})
