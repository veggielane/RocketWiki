import { useEffect, useState } from 'react'
import { useCustomEmojisQuery } from '../graphql/generated/graphql'
import {
  getEmojiRegistry,
  getEmojiRegistryVersion,
  setEmojiRegistry,
  subscribeEmojiRegistry,
} from './registry'

/**
 * Feeds the module-level registry (registry.ts) from the generated
 * `CustomEmojis` query. Mounted **once, in AppShell** — deliberately not
 * inside RichTextEditor: the editor renders in urql-less contexts (its own
 * component tests, potentially Storybook-style harnesses), and the
 * decoration/suggestion machinery reads the module store directly anyway,
 * so one feed at the shell covers every editor instance.
 */
export function useEmojiRegistryFeed(): void {
  const [{ data }] = useCustomEmojisQuery()

  useEffect(() => {
    if (data?.customEmojis) {
      setEmojiRegistry(data.customEmojis)
    }
  }, [data])
}

/**
 * Reactive read of the registry for chrome components (toolbar picker,
 * admin list previews): subscribes to the module store — no urql
 * dependency, so it's safe anywhere.
 */
export function useEmojiRegistrySnapshot(): ReadonlyMap<string, string> {
  const [, setVersion] = useState(getEmojiRegistryVersion())
  useEffect(() => subscribeEmojiRegistry(() => setVersion(getEmojiRegistryVersion())), [])
  return getEmojiRegistry()
}
