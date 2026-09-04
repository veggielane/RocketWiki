/**
 * The sentences the marking control puts in front of an editor, in one place
 * so the design-doc-reference guard (feedback/__tests__/noDesignDocRefsInCopy)
 * can walk them, and so the control's one load-bearing claim cannot drift:
 * the selectors and the eyes-only caveat decide who may read the page, and
 * the classification and the prefix decide nothing. That is this deployment's
 * access model — there is no clearance for a level to be measured against —
 * and copy that implied otherwise would send an editor looking for a check
 * that does not exist.
 */

/** Under the section heading: what a marking is made of, and which parts gate. */
export const MARKING_SECTION_DESCRIPTION =
  'A UK Government classification, optional selectors, an optional eyes-only caveat, and a UK prefix. The selectors and the eyes-only caveat decide who may read this page; the classification and the prefix are presentational and decide nothing. This is not one of the key/value properties below — it is a single value, replaced as a whole.'

/** Under the level picker: every level is offered, because none is compared against anyone. */
export const LEVEL_IS_DISPLAY_NOTE =
  'Any level may be set. Like the prefix, it is shown on the page and compared against nobody.'

/** Under the selector pickers: the one rule a value has to satisfy to be readable. */
export const SELECTOR_RULE_NOTE =
  'At most one value per category. A reader must hold an access grant in this space that carries each value.'
