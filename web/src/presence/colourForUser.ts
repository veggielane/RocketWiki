/**
 * A stable, deterministic colour per user id — same user always gets the
 * same presence colour without the server having to assign and track one.
 * Never derived from anything sensitive (nationality etc., §6.2): the
 * input is just the opaque user id, and the output is a colour, nothing
 * else about the user is encoded or recoverable from it.
 */
export function colourForUser(userId: string): string {
  let hash = 0
  for (let i = 0; i < userId.length; i++) {
    hash = (hash * 31 + userId.charCodeAt(i)) | 0
  }
  const hue = Math.abs(hash) % 360
  return `hsl(${hue}, 70%, 45%)`
}
