/**
 * design.md §8: "Pointer movement is sampled client-side (~20/sec) and
 * coalesced server-side." `record` is cheap and safe to call on every raw
 * `mousemove` (just updates a ref-like value, no network, no work) — the
 * sampler's own interval timer is what decides when a position actually
 * gets sent, and it sends at most one message per tick, always the latest
 * recorded position, never a queue of intermediate ones. A tick with no
 * new position since the last send emits nothing.
 */
export class PointerSampler {
  private readonly onSample: (x: number, y: number) => void
  private readonly intervalMs: number
  private pending: { x: number; y: number } | null = null
  private timer: ReturnType<typeof setInterval> | null = null

  constructor(onSample: (x: number, y: number) => void, intervalMs = 50 /* ~20/sec */) {
    this.onSample = onSample
    this.intervalMs = intervalMs
  }

  /** Call on every raw pointermove/mousemove event — cheap, no side effects beyond recording the latest position. */
  record(x: number, y: number): void {
    this.pending = { x, y }
  }

  start(): void {
    if (this.timer) return
    this.timer = setInterval(() => {
      if (!this.pending) return
      const { x, y } = this.pending
      this.pending = null
      this.onSample(x, y)
    }, this.intervalMs)
  }

  stop(): void {
    if (this.timer) {
      clearInterval(this.timer)
      this.timer = null
    }
    this.pending = null
  }
}
