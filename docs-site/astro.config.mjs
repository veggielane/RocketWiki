// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { SITE, BASE, REPO_URL } from './scripts/site-constants.mjs';

// GitHub Pages project site: served at https://veggielane.github.io/RocketWiki/.
// `site` + `base` come from scripts/site-constants.mjs so the generated
// content's links and Astro's routing can never disagree about the base path.
export default defineConfig({
	site: SITE,
	base: BASE,
	integrations: [
		starlight({
			title: 'RocketWiki',
			description:
				'A self-hosted Confluence replacement with Markdown storage, GraphQL, and attribute-based access control.',
			social: [{ icon: 'github', label: 'GitHub', href: REPO_URL }],
			sidebar: [
				{
					label: 'Start Here',
					items: [
						{ label: 'Welcome', link: '/' },
						{ label: 'Documentation map', slug: 'docs-map' },
						{ label: 'Developing locally', slug: 'developing' },
						{ label: 'Current status', slug: 'status' },
					],
				},
				{
					label: 'User Guide',
					// The in-app help (web/src/help), the same content the app serves
					// at /-/docs, generated into guide/** at build time. The two
					// sub-groups mirror the two sections in web/src/help/manifest.json;
					// add a section there and add its group here. Topic order within a
					// group comes from each page's sidebar.order (set from the manifest).
					items: [
						{ label: 'Using RocketWiki', items: [{ autogenerate: { directory: 'guide/using-rocketwiki' } }] },
						{ label: 'Administering RocketWiki', items: [{ autogenerate: { directory: 'guide/administering-rocketwiki' } }] },
					],
				},
				{
					label: 'Design',
					collapsed: true,
					// Generated pages: design/00-overview + one page per design.md
					// `## N.` section, zero-padded so file order == section order.
					items: [{ autogenerate: { directory: 'design' } }],
				},
				{
					label: 'Data Model',
					items: [{ label: 'EF Core / SQL Server schema', slug: 'data-model' }],
				},
				{
					label: 'Operations',
					items: [
						{ label: 'Deploying on k3s', slug: 'operations/deploy' },
						{ label: 'Dev Keycloak realm', slug: 'operations/keycloak' },
					],
				},
				{ label: 'GitHub repository', link: REPO_URL, attrs: { target: '_blank' } },
			],
		}),
	],
});
