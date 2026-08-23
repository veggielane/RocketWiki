// Single source of truth for the deployed site's location. Imported by both
// astro.config.mjs and the content generator so page links and the Astro
// `base` can never drift apart. GitHub Pages serves project sites under
// /<repo>/ — the classic footgun this file exists to defuse.
export const SITE = 'https://veggielane.github.io';
export const BASE = '/RocketWiki';
export const REPO_URL = 'https://github.com/veggielane/RocketWiki';
export const BLOB_BASE = `${REPO_URL}/blob/main`;
