# Dev Keycloak realm

`rocketwiki-realm.json` is imported automatically when the AppHost starts the
Keycloak container (`AppHost.cs`, via `.WithImport(...)`), giving `aspire run`
a working realm out of the box — no manual admin-console setup for local dev.

**This file is dev-only.** It is bind-mounted into the container and imported
with `--import-realm` every time the container starts fresh (or whenever the
data volume is reset); it is not how a real deployment gets its realm.

## What's in it

- Realm `rocketwiki`.
- Two clients:
  - `rocketwiki-web` — the SPA. Public client, Authorization Code + PKCE
    (`pkce.code.challenge.method: S256`), no client secret. Redirect URI and
    web origin point at the Vite dev server (`http://localhost:5173`) — update
    these if the frontend's dev port ever changes.
  - `rocketwiki-api` — `bearerOnly`. Never used to run a flow; it exists only
    as an audience target (see below) and as the client MCP's OAuth 2.1 flow
    will eventually authenticate against (design.md §8).
- A shared client scope, `rocketwiki-claims`, attached to `rocketwiki-web`,
  carrying the three protocol mappers that make the ABAC engine possible
  (design.md §6, §11 step 5):
  - **`groups`** (`oidc-group-membership-mapper`, `full.path: false`) — group
    *names*, not paths, so they match rule expressions like
    `{"group": "engineering"}` directly.
  - **`nationality`** (`oidc-usermodel-attribute-mapper`, `multivalued: true`)
    — always emitted as a JSON array, per data-model.md's `string[]` typing
    for dual nationals.
  - **`audience-rocketwiki-api`** (`oidc-audience-mapper`) — adds
    `rocketwiki-api` to the access token's `aud` claim, since the SPA client
    that actually mints the token isn't the API's own client. RocketWiki.Api's
    `Keycloak:Audience` config must match this string (`appsettings.json`).
- Two realm roles (`admin`, `user`) and a `default-roles-rocketwiki` composite
  granting `user` to every account, matching design.md §6.5's instance-role
  model — `admin` here is a Keycloak role, not an ABAC bypass; RocketWiki
  deliberately never lets it skip page restrictions.
- Two groups (`engineering`, `export-cleared`) and six dev users, picked to
  exercise the rule engine's edge cases rather than just the happy path:

  | Username | Groups | `nationality` | Why |
  |---|---|---|---|
  | `alice.engineer` | engineering | `["NZ"]` | baseline allow |
  | `bob.engineer` | engineering | `["US"]` | baseline allow, different value |
  | `carol.noattr` | engineering | *(none)* | **fail-closed case (§6.3)**: no `nationality` claim at all must match no `attr` condition, not throw and not default-allow |
  | `dave.dual` | engineering | `["NZ","GB"]` | multivalued claim; also exercises the open question in design.md §17 on whether `in` matches *any* held nationality |
  | `erin.export` | export-cleared (not engineering) | `["US"]` | satisfies an `anyOf` group branch without satisfying an `allOf` engineering branch |
  | `frank.admin` | *(none)* | *(none)* | realm role `admin`, no groups — proves an admin with no matching group still fails ABAC checks (§6.5 "no bypass") |

  All six share the password `RocketWiki!Dev1`, chosen for one place to look
  it up, not for actual users to reuse. **Never use this realm, these users,
  or this password outside local development.**

## What's unverified

No Docker is available in the environment this file was authored in, so the
import has **not** been exercised against a running Keycloak container — the
JSON was checked for syntactic validity only (`JSON.parse`), not by actually
starting Keycloak and inspecting a minted token. Keycloak's realm-export
schema is generally stable, but has drifted in small ways across major
versions; this file targets what `AddKeycloakContainer` in
`Keycloak.AuthServices.Aspire.Hosting` 0.3.0 pins by default:
**`quay.io/keycloak/keycloak:26.6.1`**. If the AppHost's Keycloak tag ever
changes, re-validate this file against that version — in particular, protocol
mapper `protocolMapper` type names (`oidc-group-membership-mapper`,
`oidc-usermodel-attribute-mapper`, `oidc-audience-mapper`) and default
client-scope names (`acr`, `basic`, `email`, `profile`, `roles`,
`web-origins`) are the parts most likely to move between versions.

Before trusting this for anything beyond "does login work at all," actually
run `aspire run`, log in as each user, decode the resulting access token, and
confirm `groups`/`nationality`/`aud` look as documented above.

## What a production Keycloak needs

Production Keycloak is an **existing external service**, not a container
Aspire runs (design.md §15) — this file is never imported into it. Whoever
owns that realm needs to reproduce the same three things by hand (or via
Keycloak's own export/import, or Terraform/the Keycloak admin API):

1. **The same three protocol mappers**, on whatever client(s) the production
   SPA and API actually use — `groups` (full path off), `nationality`
   (multivalued), and an audience mapper naming the production API client.
   Without these, the token never carries the claims RocketWiki's rule engine
   evaluates, and every rule referencing them fails closed (matches nobody).
2. **A `nationality` user attribute** (or whatever the site's attribute
   registry, design.md §6.2, ultimately declares) populated for every user
   who needs an `attr` rule to resolve — populated in Keycloak itself, since
   design.md §6.2 makes Keycloak the single source of truth for these values.
   `string[]`-typed attributes (dual nationals) need the matching multivalued
   mapper config, same as here.
3. **`groups` and `export-cleared`/`engineering`-equivalent groups**, or
   whatever the real org's group structure is — names only need to match what
   RocketWiki's access rules reference; RocketWiki has no opinion on Keycloak
   group hierarchy beyond that.

Two things this dev realm does that production must **not** silently copy:
`sslRequired: "none"` (production Keycloak must require TLS), and the shared
dev password. Neither belongs anywhere near a real deployment.
