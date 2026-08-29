/** One revision's worth of save feedback, whoever triggered it. */
export interface SaveOutcome {
  revisionNumber: number
  contributors: string[]
  auto: boolean
}

/**
 * The save notice, in one place.
 *
 * Two surfaces render it and they are in different components: an autosave's
 * notice appears in the editor's own Snackbar, while a manual save's has to
 * survive the navigation to the page view (it travels as router state, because
 * the editor unmounts). Composing it here is what stops those becoming two
 * sentences.
 *
 * Its own module rather than an export from PageEditPage: a component file that
 * also exports a function breaks React Fast Refresh for the whole file.
 */
export function describeSave({ revisionNumber, contributors, auto }: SaveOutcome): string {
  const credit = contributors.length > 0 ? ` — contributors: ${contributors.join(', ')}` : ''
  return `${auto ? 'Autosaved' : 'Saved'} revision ${revisionNumber}${credit}`
}
