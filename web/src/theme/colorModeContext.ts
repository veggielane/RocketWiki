import { createContext, useContext } from 'react'
import type { ThemeMode } from './theme'

export interface ColorModeContextValue {
  mode: ThemeMode
  toggle: () => void
}

export const ColorModeContext = createContext<ColorModeContextValue | null>(null)

export function useColorMode(): ColorModeContextValue {
  const ctx = useContext(ColorModeContext)
  if (!ctx) {
    throw new Error('useColorMode must be used within a ColorModeProvider')
  }
  return ctx
}
