import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PointerSampler } from '../pointerSampler'

describe('PointerSampler', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('sends nothing before the first tick, even if positions were recorded', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    sampler.record(10, 20)
    expect(onSample).not.toHaveBeenCalled()
  })

  it('sends the latest recorded position on the next tick', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    sampler.record(10, 20)
    vi.advanceTimersByTime(50)
    expect(onSample).toHaveBeenCalledExactlyOnceWith(10, 20)
  })

  it('coalesces many rapid recordings into a single send per tick — never one message per mousemove', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    for (let i = 0; i < 100; i++) {
      sampler.record(i, i)
    }
    vi.advanceTimersByTime(50)
    expect(onSample).toHaveBeenCalledTimes(1)
    expect(onSample).toHaveBeenCalledWith(99, 99)
  })

  it('sends nothing on a tick where the position has not changed since the last send', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    sampler.record(10, 20)
    vi.advanceTimersByTime(50)
    vi.advanceTimersByTime(50) // no new record() call in between
    expect(onSample).toHaveBeenCalledTimes(1)
  })

  it('resumes sending once movement starts again after a quiet period', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    sampler.record(1, 1)
    vi.advanceTimersByTime(50)
    vi.advanceTimersByTime(150) // three quiet ticks
    sampler.record(2, 2)
    vi.advanceTimersByTime(50)
    expect(onSample).toHaveBeenCalledTimes(2)
    expect(onSample).toHaveBeenLastCalledWith(2, 2)
  })

  it('samples at roughly 20/sec by default', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample)
    sampler.start()
    for (let t = 0; t < 1000; t += 10) {
      sampler.record(t, t)
      vi.advanceTimersByTime(10)
    }
    // ~1000ms of continuous movement at the default interval should yield
    // roughly 20 sends, not one per 10ms recording (100 of those occurred).
    expect(onSample.mock.calls.length).toBeGreaterThan(15)
    expect(onSample.mock.calls.length).toBeLessThan(25)
  })

  it('stop() clears the pending position and stops future sends', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    sampler.record(5, 5)
    sampler.stop()
    vi.advanceTimersByTime(200)
    expect(onSample).not.toHaveBeenCalled()
  })

  it('calling start() twice does not create duplicate timers', () => {
    const onSample = vi.fn()
    const sampler = new PointerSampler(onSample, 50)
    sampler.start()
    sampler.start()
    sampler.record(1, 1)
    vi.advanceTimersByTime(50)
    expect(onSample).toHaveBeenCalledTimes(1)
  })
})
