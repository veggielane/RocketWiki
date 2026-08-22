import type { PointerPosition, PresenceTransport, PresenceViewer } from './types'

/**
 * Stands in for a real presence hub connection (design.md §8, milestone
 * 4b — no hub exists yet). Tracks joined pages and sent pointer positions
 * so tests can assert teardown actually happens, which is the entire
 * point of this feature ("a leaked hub subscription is a live data leak,
 * not just a memory leak" — design.md §8).
 */
export class FakePresenceTransport implements PresenceTransport {
  private viewerHandlers = new Set<(viewers: PresenceViewer[]) => void>()
  private pointerHandlers = new Set<(position: PointerPosition) => void>()

  joinedPages: string[] = []
  leftPages: string[] = []
  sentPositions: { x: number; y: number }[] = []

  async joinPage(pageId: string): Promise<void> {
    this.joinedPages.push(pageId)
  }

  async leavePage(pageId: string): Promise<void> {
    this.leftPages.push(pageId)
  }

  /** Currently-joined pages, accounting for leaves — for tests asserting "no page left joined after teardown." */
  get currentlyJoinedPages(): string[] {
    const left = [...this.leftPages]
    return this.joinedPages.filter((pageId) => {
      const i = left.indexOf(pageId)
      if (i === -1) return true
      left.splice(i, 1)
      return false
    })
  }

  onViewersChanged(handler: (viewers: PresenceViewer[]) => void): () => void {
    this.viewerHandlers.add(handler)
    return () => this.viewerHandlers.delete(handler)
  }

  onPointerMoved(handler: (position: PointerPosition) => void): () => void {
    this.pointerHandlers.add(handler)
    return () => this.pointerHandlers.delete(handler)
  }

  sendPointerPosition(x: number, y: number): void {
    this.sentPositions.push({ x, y })
  }

  /** Test/dev only: simulates the hub broadcasting an updated viewer list. */
  emitViewers(viewers: PresenceViewer[]): void {
    for (const handler of this.viewerHandlers) {
      handler(viewers)
    }
  }

  /** Test/dev only: simulates another viewer's pointer position arriving. */
  emitPointer(position: PointerPosition): void {
    for (const handler of this.pointerHandlers) {
      handler(position)
    }
  }
}
