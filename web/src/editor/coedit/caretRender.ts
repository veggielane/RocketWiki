import { getAvatarUrl, peekAvatarUrl } from '../../avatars/avatarCache'
import { readableTextOn } from '../../presence/readableTextOn'

/**
 * The identity a client announces on the awareness channel (the caret
 * label). Deliberately minimal — the presence payload rule (design.md §8)
 * applies verbatim: display name and colour, plus the OPAQUE local user id
 * that `ViewersChanged` already exposes to everyone in the room, so caret
 * labels can show the same avatar the presence chips do. Never attributes
 * (§6.2), never content; the avatar bytes themselves come from the
 * authenticated `/users/{id}/avatar` route via the session blob cache, not
 * from the awareness payload.
 */
export interface CaretUser {
  name: string
  color: string
  userId?: string
}

/**
 * Custom caret DOM for CollaborationCaret: the standard caret + label
 * shape (styled in editor-content.css), with the sender's avatar in the
 * label when one resolves. Plain DOM, not React — the caret extension
 * renders through ProseMirror widget decorations. Avatar loading is
 * fire-and-forget against the session cache (one fetch per user per
 * session, avatars/avatarCache.ts): until it settles — or if the user has
 * no avatar — the label is just name-on-colour, which is the complete
 * fallback, not an error state.
 */
export function renderCaret(user: Record<string, unknown>): HTMLElement {
  const { name, color, userId } = user as Partial<CaretUser>

  const caret = document.createElement('span')
  caret.classList.add('collaboration-carets__caret')
  caret.style.borderColor = color ?? 'currentColor'

  const label = document.createElement('div')
  label.classList.add('collaboration-carets__label')
  label.style.backgroundColor = color ?? 'currentColor'
  if (color) {
    // The CSS default is white text; adapt to the assigned colour so light
    // hues keep WCAG 1.4.3 contrast (see presence/readableTextOn.ts).
    label.style.color = readableTextOn(color)
  }

  if (userId) {
    const img = document.createElement('img')
    img.classList.add('collaboration-carets__avatar')
    img.alt = ''
    img.draggable = false
    const cached = peekAvatarUrl(userId)
    if (cached) {
      img.src = cached
      label.appendChild(img)
    } else if (cached !== null) {
      // Unknown yet — resolve through the cache; append only on success so
      // a no-avatar user never shows a broken-image glyph.
      void getAvatarUrl(userId)
        .then((url) => {
          if (url && label.isConnected) {
            img.src = url
            label.insertBefore(img, label.firstChild)
          }
        })
        .catch(() => {})
    }
  }

  label.appendChild(document.createTextNode(name ?? 'Someone'))
  caret.appendChild(label)
  return caret
}
