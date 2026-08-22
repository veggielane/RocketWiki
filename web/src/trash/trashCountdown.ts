/**
 * Days remaining in the 30-day trash window (data-model.md), rounded up —
 * "expires in 1 day" for anything under 24h remaining reads better than
 * "expires in 0 days" for something that's actually still hours away.
 * Negative/zero means it's already past expiry (shown as "expired").
 */
export function daysUntil(expiresAtUtc: string, now: Date = new Date()): number {
  const msRemaining = new Date(expiresAtUtc).getTime() - now.getTime()
  return Math.ceil(msRemaining / (1000 * 60 * 60 * 24))
}

export function describeExpiry(expiresAtUtc: string, now: Date = new Date()): string {
  const days = daysUntil(expiresAtUtc, now)
  if (days <= 0) return 'Expired'
  if (days === 1) return 'Expires in 1 day'
  return `Expires in ${days} days`
}
