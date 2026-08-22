import { describe, expect, it } from 'vitest'
import { formatBytes } from '../formatBytes'

describe('formatBytes', () => {
  it('shows bytes under 1000 as-is', () => {
    expect(formatBytes(512)).toBe('512 B')
  })

  it('shows KB with one decimal under 10', () => {
    expect(formatBytes(1500)).toBe('1.5 KB')
  })

  it('shows KB with no decimal at 10 or above', () => {
    expect(formatBytes(15_000)).toBe('15 KB')
  })

  it('rolls over to MB', () => {
    expect(formatBytes(1_500_000)).toBe('1.5 MB')
  })

  it('rolls over to GB', () => {
    expect(formatBytes(1_500_000_000)).toBe('1.5 GB')
  })
})
