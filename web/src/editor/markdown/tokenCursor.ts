import type { Token } from 'markdown-it'

/**
 * markdown-it's `parse()` returns a *flat* array of open/self-closing/close
 * tokens (each carrying a `.level`, ProseMirror-style trees have to be
 * rebuilt from that). This cursor plus the "consume until nesting === -1"
 * pattern in fromMarkdown.ts is the standard trick for doing that
 * recursively without tracking levels by hand at every call site: a
 * container's matching close is always the point where a linear scan next
 * sees `nesting === -1`, because any nested container's own close was
 * already consumed by the recursive call that parsed it.
 */
export class TokenCursor {
  private readonly tokens: Token[]
  private index = 0

  constructor(tokens: Token[]) {
    this.tokens = tokens
  }

  peek(): Token {
    const tok = this.tokens[this.index]
    if (!tok) {
      throw new Error('Unexpected end of markdown token stream')
    }
    return tok
  }

  next(): Token {
    const tok = this.peek()
    this.index += 1
    return tok
  }

  hasNext(): boolean {
    return this.index < this.tokens.length
  }
}
