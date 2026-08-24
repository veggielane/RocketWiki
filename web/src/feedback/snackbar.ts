/**
 * One auto-hide duration for every snackbar in the app (see the
 * "UI feedback conventions" section of web/README.md: snackbar = transient
 * notice). Two surfaces independently picked 5s and 6s during the feature
 * waves; 6s won — WCAG 2.2's timing guidance favors the longer window, and
 * a snackbar that sometimes lingers longer than others reads as a bug.
 */
export const SNACKBAR_AUTO_HIDE_MS = 6000
