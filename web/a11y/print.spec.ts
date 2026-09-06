import { existsSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { test, expect, type Locator, type Page } from '@playwright/test'
import * as pdfjs from 'pdfjs-dist/legacy/build/pdf.mjs'

/**
 * Printing (web/src/theme/print.ts, AppShell.tsx): a page prints in full,
 * across as many sheets as it needs, with the protective marking at the head
 * and the foot of the printed document and the navigation furniture left off.
 *
 * Checked the way it failed: under print-media emulation in real Chromium,
 * over a capture made many sheets tall by cloning its rendered content, with
 * a sentinel paragraph at the very end whose position proves the end of the
 * document is in the printed flow rather than beyond a clipped region — and
 * finished with a real PDF render whose sheet count is asserted. "It looks
 * fine" is how a one-screen print shipped; these are measurements.
 *
 * Print rendering does not depend on the OS colour scheme, so the os-dark
 * project skips this file rather than re-checking agreement.
 */
const here = dirname(fileURLToPath(import.meta.url))
const screensDir = process.env.A11Y_SCREENS_DIR ?? join(here, 'screens')
const SENTINEL = 'END-OF-PRINTED-DOCUMENT-SENTINEL'
/** Rendered-content clones appended to the page body: ~20 A4 sheets. */
const CLONES = 30
/** The capture viewport, and what a one-screen print would be limited to. */
const VIEWPORT_HEIGHT = 720

test.beforeEach(() => {
  test.skip(test.info().project.name === 'os-dark', 'print rendering is colour-scheme independent')
})

async function open(page: Page, file: string, tall: boolean) {
  const path = join(screensDir, file)
  test.skip(!existsSync(path), `${file} not captured`)
  await page.setViewportSize({ width: 1280, height: VIEWPORT_HEIGHT })
  await page.goto(pathToFileURL(path).href)
  await page.waitForLoadState('load')
  await page.evaluate(
    ({ clones, sentinel }) => {
      const main = document.querySelector('main#main-content')!
      const body = main.querySelector('.ProseMirror')!
      const original = body.innerHTML
      for (let i = 0; i < clones; i++) {
        const clone = document.createElement('div')
        clone.innerHTML = original
        body.appendChild(clone)
      }
      const p = document.createElement('p')
      p.id = 'print-sentinel'
      p.textContent = sentinel
      body.appendChild(p)
    },
    { clones: tall ? CLONES : 0, sentinel: SENTINEL },
  )
}

/**
 * Present in the DOM and not rendered — as opposed to `toBeHidden()` alone,
 * which is also satisfied by a control that was never there, and would let a
 * renamed button turn this whole check vacuous.
 */
async function expectPresentButHidden(locator: Locator) {
  // Role locators drop hidden elements unless told otherwise, so the callers
  // pass `includeHidden: true` — without it "attached" is exactly what a
  // hidden control fails.
  await expect(locator).toBeAttached()
  await expect(locator).toBeHidden()
}

/**
 * What a PDF actually carries: its text with all whitespace removed (a cell
 * wrapped per character comes out as one glyph per line, and the check is
 * for presence, not typography), and the drawn width of every raster image
 * in sheet points — the content stream's own units sit under a base
 * transform, so the full matrix is tracked through save/restore.
 */
async function readPdf(pdf: Buffer) {
  const doc = await pdfjs.getDocument({ data: new Uint8Array(pdf) }).promise
  let text = ''
  const images: { sheet: number; drawnWidthPt: number; sheetWidthPt: number }[] = []
  const mul = (m: number[], n: number[]) => [
    m[0] * n[0] + m[2] * n[1], m[1] * n[0] + m[3] * n[1],
    m[0] * n[2] + m[2] * n[3], m[1] * n[2] + m[3] * n[3],
    m[0] * n[4] + m[2] * n[5] + m[4], m[1] * n[4] + m[3] * n[5] + m[5],
  ]
  const paints = new Set([pdfjs.OPS.paintImageXObject, pdfjs.OPS.paintJpegXObject, pdfjs.OPS.paintInlineImageXObject, pdfjs.OPS.paintImageXObjectRepeat])
  for (let i = 1; i <= doc.numPages; i++) {
    const page = await doc.getPage(i)
    const content = await page.getTextContent()
    text += content.items.map((item) => ('str' in item ? item.str : '')).join('')
    const ops = await page.getOperatorList()
    const sheetWidthPt = page.getViewport({ scale: 1 }).width
    let ctm = [1, 0, 0, 1, 0, 0]
    const stack: number[][] = []
    for (let k = 0; k < ops.fnArray.length; k++) {
      const fn = ops.fnArray[k]
      if (fn === pdfjs.OPS.save) stack.push(ctm)
      else if (fn === pdfjs.OPS.restore) ctm = stack.pop() ?? ctm
      else if (fn === pdfjs.OPS.transform) ctm = mul(ctm, ops.argsArray[k] as number[])
      else if (paints.has(fn)) images.push({ sheet: i, drawnWidthPt: Math.hypot(ctm[0], ctm[1]), sheetWidthPt })
    }
  }
  return { sheets: doc.numPages, text: text.replace(/\s+/g, ''), images }
}

/** Geometry of the things the print layout is about, in the current media. */
function measure(page: Page) {
  return page.evaluate(() => {
    const rect = (el: Element | null) => {
      if (!el) return null
      const b = el.getBoundingClientRect()
      return { top: Math.round(b.top), bottom: Math.round(b.bottom) }
    }
    const main = document.querySelector('main#main-content')!
    const column = main.parentElement!.parentElement!
    const banner = document.querySelector('[data-classification-banner="foot"]')
    const head = document.querySelector('[data-classification-banner="print-head"]')
    const sentinel = document.getElementById('print-sentinel')
    return {
      main: { overflowY: getComputedStyle(main).overflowY, clientHeight: main.clientHeight, scrollHeight: main.scrollHeight, rect: rect(main)! },
      column: { height: Math.round(column.getBoundingClientRect().height), display: getComputedStyle(column).display, lastChild: column.lastElementChild?.getAttribute('data-classification-banner') ?? null },
      docHeight: document.documentElement.scrollHeight,
      sentinel: rect(sentinel),
      banner: rect(banner),
      head: head ? { ...rect(head)!, display: getComputedStyle(head).display } : null,
      regions: document.querySelectorAll('section[aria-label="Protective marking for this page"]').length,
    }
  })
}

test.describe('printing a page', () => {
  test('a long page prints in full: the scroll region neither scrolls nor clips, and the last line is in the flow', async ({ page }) => {
    await open(page, 'page-view--light.html', true)
    await page.emulateMedia({ media: 'print' })
    const m = await measure(page)
    // Not a scroll container on paper, and nothing hidden inside it.
    expect(m.main.overflowY).toBe('visible')
    expect(m.main.scrollHeight).toBeLessThanOrEqual(m.main.clientHeight + 1)
    // The end of the document is inside the region's box and inside the
    // document, many screens down — not beyond a one-screen clip.
    expect(m.sentinel).not.toBeNull()
    expect(m.sentinel!.bottom).toBeLessThanOrEqual(m.main.rect.bottom + 1)
    expect(m.sentinel!.bottom).toBeLessThanOrEqual(m.docHeight)
    expect(m.sentinel!.bottom).toBeGreaterThan(VIEWPORT_HEIGHT * 5)
    expect(m.column.height).toBeGreaterThan(VIEWPORT_HEIGHT * 5)
  })

  test('the protective marking heads and foots the printed document', async ({ page }) => {
    await open(page, 'page-view--light.html', true)
    await page.emulateMedia({ media: 'print' })
    const m = await measure(page)
    // The print-only head copy is the first thing on the first sheet — above
    // the content region, not after it (it is moved with `order`, which
    // needs the shell to stay a flex column when printed).
    expect(m.head).not.toBeNull()
    expect(m.head!.display).not.toBe('none')
    expect(m.head!.top).toBe(0)
    expect(m.head!.bottom).toBeLessThanOrEqual(m.main.rect.top + 1)
    // The banner proper is the last thing, after the last line of content.
    expect(m.banner).not.toBeNull()
    expect(m.banner!.top).toBeGreaterThanOrEqual(m.sentinel!.bottom - 1)
    expect(m.column.lastChild).toBe('print-head')
    // Still exactly one landmark: the head copy is aria-hidden.
    expect(m.regions).toBe(1)
  })

  test('navigation furniture stays off paper; the breadcrumb, the title and the content print', async ({ page }) => {
    await open(page, 'page-view--light.html', false)
    await page.emulateMedia({ media: 'print' })
    await expectPresentButHidden(page.locator('.MuiDrawer-root'))
    await expectPresentButHidden(page.getByRole('button', { name: /collapse navigation|expand navigation/i, includeHidden: true }))
    await expectPresentButHidden(page.getByLabel('Search the wiki'))
    // The page's own action row (watch, edit, presence, the overflow menu).
    await expectPresentButHidden(page.getByRole('button', { name: /^watch(ing)?$/i, includeHidden: true }))
    await expectPresentButHidden(page.getByRole('button', { name: /^(edit|add) labels$/i, includeHidden: true }))
    await expectPresentButHidden(page.getByRole('button', { name: 'Post comment', includeHidden: true }))
    await expectPresentButHidden(page.getByRole('button', { name: 'Reply', includeHidden: true }).first())
    await expectPresentButHidden(page.getByText('Upload attachment'))
    await expect(page.getByRole('navigation', { name: /breadcrumb/i })).toBeVisible()
    // The page title (the content's own `#` heading is a second h1).
    await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible()
    await expect(page.locator('main .ProseMirror').first()).toBeVisible()
  })

  test('the editor prints its document, not its toolbar or save bar', async ({ page }) => {
    await open(page, 'page-edit--light.html', false)
    await page.emulateMedia({ media: 'print' })
    await expectPresentButHidden(page.getByRole('toolbar', { name: 'Formatting', includeHidden: true }))
    await expectPresentButHidden(page.getByRole('button', { name: 'Save', exact: true, includeHidden: true }))
    await expect(page.locator('main .ProseMirror').first()).toBeVisible()
  })

  test('print rules do not leak into screen media', async ({ page }) => {
    await open(page, 'page-view--light.html', true)
    await page.emulateMedia({ media: 'screen' })
    const m = await measure(page)
    expect(m.main.overflowY).toBe('auto')
    expect(m.main.scrollHeight).toBeGreaterThan(m.main.clientHeight)
    expect(m.column.height).toBe(VIEWPORT_HEIGHT)
    expect(m.head!.display).toBe('none')
    await expect(page.locator('.MuiDrawer-root')).toBeVisible()
    await expect(page.getByRole('button', { name: /^watch(ing)?$/i })).toBeVisible()
    await expect(page.getByRole('toolbar', { name: 'Formatting' })).toHaveCount(0)
  })

  test('a long page renders to many sheets', async ({ page }, testInfo) => {
    await open(page, 'page-view--light.html', true)
    const pdf = await page.pdf({ format: 'A4' })
    // Chromium's PDF writer emits one `/Type /Page` object per sheet.
    const sheets = (pdf.toString('latin1').match(/\/Type\s*\/Page(?!s)/g) ?? []).length
    testInfo.annotations.push({ type: 'sheets', description: String(sheets) })
    expect(sheets).toBeGreaterThan(5)
  })

  // Wide content (editor-content.css, `@media print`): complete beats cut.
  // A twelve-column table is the realistic cramped case — at twenty-four the
  // cells wrap one glyph per line and the PDF's text is no longer something a
  // string search can read, though the columns are all there.
  test('a wide table prints every column, cramped rather than cut', async ({ page }) => {
    await open(page, 'page-view--light.html', false)
    await page.evaluate(() => {
      const body = document.querySelector('main#main-content .ProseMirror')!
      const cols = 12
      const cell = (tag: string, c: number, row: number) =>
        `<${tag} colspan="1" rowspan="1"><p>${c === cols - 1 ? (row === 0 ? 'HEADSENTINEL' : 'LASTCELLSENTINEL') : `column${String(c + 1).padStart(2, '0')}text`}</p></${tag}>`
      const row = (tag: string, r: number) => Array.from({ length: cols }, (_, c) => cell(tag, c, r)).join('')
      // The editor's own table markup: wrapper, min-width, one <col> per column.
      const wrapper = document.createElement('div')
      wrapper.className = 'tableWrapper'
      wrapper.id = 'wide-table'
      wrapper.innerHTML = `<table style="min-width: ${cols * 25}px"><colgroup>${'<col style="min-width: 25px">'.repeat(cols)}</colgroup><tbody><tr>${row('th', 0)}</tr><tr>${row('td', 1)}</tr></tbody></table>`
      body.appendChild(wrapper)
    })
    // On the sheet itself: the last column, header and body, is printed.
    const pdf = await readPdf(await page.pdf({ format: 'A4' }))
    expect(pdf.text).toContain('HEADSENTINEL')
    expect(pdf.text).toContain('LASTCELLSENTINEL')
    expect(pdf.text).toContain('column11text')
    // And the layout that gets it there: fixed columns inside the measure.
    await page.emulateMedia({ media: 'print' })
    const layout = await page.evaluate(() => {
      const table = document.querySelector('#wide-table table')!
      const main = document.querySelector('main#main-content')!
      return { tableLayout: getComputedStyle(table).tableLayout, tableRight: table.getBoundingClientRect().right, mainRight: main.getBoundingClientRect().right }
    })
    expect(layout.tableLayout).toBe('fixed')
    expect(layout.tableRight).toBeLessThanOrEqual(layout.mainRight + 1)
  })

  test('an oversized image and a wide diagram print whole, scaled to the sheet', async ({ page }) => {
    await open(page, 'page-view--light.html', false)
    // A real raster (so the PDF carries it as an image object whose drawn
    // width can be read back), declared 3000px wide; and a diagram-shaped
    // inline SVG with a viewBox and text at its far right edge.
    const png = 'data:image/png;base64,' + (await page.screenshot({ clip: { x: 0, y: 0, width: 4, height: 4 } })).toString('base64')
    await page.evaluate((png) => {
      const body = document.querySelector('main#main-content .ProseMirror')!
      const img = document.createElement('img')
      img.id = 'wide-image'
      img.width = 3000
      img.height = 120
      img.src = png
      body.appendChild(img)
      const svg = document.createElement('div')
      svg.id = 'wide-diagram'
      svg.innerHTML =
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 3000 60" width="3000" height="60"><text x="20" y="35" font-size="20">DIAGRAMLEFT</text><text x="2700" y="35" font-size="20">DIAGRAMRIGHTSENTINEL</text></svg>'
      body.appendChild(svg)
    }, png)
    const pdf = await readPdf(await page.pdf({ format: 'A4' }))
    // The diagram's far-right text reaches the sheet, and the raster image is
    // drawn no wider than a sheet.
    expect(pdf.text).toContain('DIAGRAMRIGHTSENTINEL')
    expect(pdf.images.length).toBeGreaterThanOrEqual(1)
    for (const image of pdf.images) expect(image.drawnWidthPt).toBeLessThanOrEqual(image.sheetWidthPt)
    // And the layout that gets them there.
    await page.emulateMedia({ media: 'print' })
    const layout = await page.evaluate(() => {
      const main = document.querySelector('main#main-content')!.getBoundingClientRect()
      const right = (sel: string) => document.querySelector(sel)!.getBoundingClientRect().right
      return { mainRight: main.right, imageRight: right('#wide-image'), diagramRight: right('#wide-diagram svg') }
    })
    expect(layout.imageRight).toBeLessThanOrEqual(layout.mainRight + 1)
    expect(layout.diagramRight).toBeLessThanOrEqual(layout.mainRight + 1)
  })
})
