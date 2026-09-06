/**
 * What a printed page carries, and what it does not.
 *
 * Printing is for the content and its protective marking: the breadcrumb (where
 * this is), the title, the body, its labels and properties, the comments, and
 * the classification banner at the head and foot of the printed document. The
 * frame around them — the navigation rail, the header strip's search box and
 * buttons, a screen's action buttons, comment composers, the editor's toolbar
 * and save bar — is furniture for a person at a screen, and on paper it is
 * ink spent on controls nobody can press. Those get this fragment.
 *
 * Deliberately NOT a blanket `button { display: none }`: some buttons carry the
 * content's structure (an accordion's summary is its section title, a tab is
 * a selected view) and hiding them would hide meaning. Each furniture site
 * opts in, so a new control prints until someone decides it should not — the
 * safe default for a document that has to be complete.
 *
 * Spread into `sx`: `sx={{ ...PRINT_HIDDEN }}` or `sx={{ ..., ...PRINT_HIDDEN }}`.
 */
export const PRINT_HIDDEN = { '@media print': { display: 'none' } } as const
