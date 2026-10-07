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
    (`pkce.code.challenge.method: S256`), no client secret. Redirect URIs and
    web origins name the two origins the SPA runs on under the AppHost:
    `http://localhost:5173` (the web container, pinned in `AppHost.cs`) and
    `http://localhost:5174` (where `npm run dev` lands while the container
    owns 5173) — update these if either port ever changes.
  - `rocketwiki-api` — `bearerOnly`. Never used to run a flow; it exists only
    as an audience target (see below) and as the client MCP's OAuth 2.1 flow
    will eventually authenticate against (design.md §8).
- Protocol mappers **on the `rocketwiki-web` client itself**, not on a shared
  client scope. That placement is load-bearing and was learned the hard way —
  see "The client-scope trap" below. They are what makes the ABAC engine
  possible (design.md §6, §11 step 5):
  - **`groups`** (`oidc-group-membership-mapper`, `full.path: false`) — group
    *names*, not paths, so they match rule expressions like
    `{"group": "engineering"}` directly.
  - **`nationality`** (`oidc-usermodel-attribute-mapper`, `multivalued: true`)
    — always emitted as a JSON array, per data-model.md's `string[]` typing
    for dual nationals. It is what the eyes-only caveat (design.md §21.4)
    compares against and what `attr` rule conditions read, so its values are
    drawn from the fixed five caveat tokens `AUS`, `CAN`, `NZ`, `UK`, `US`.
  - **`roles`** (`oidc-usermodel-realm-role-mapper`, `multivalued: true`) — a
    *flat* claim rather than Keycloak's nested `realm_access.roles`, which the
    JWT bearer handler would never flatten. This is what
    `IInstanceRoleAccessor` reads (design.md §6.5). Note the second half of
    that story: ASP.NET *does* rename `roles` to `ClaimTypes.Role` on the way
    in, so the accessor has to accept both spellings — it originally didn't,
    and no caller was ever an instance admin.
  - **`audience-rocketwiki-api`** (`oidc-audience-mapper`) — adds
    `rocketwiki-api` to the access token's `aud` claim, since the SPA client
    that actually mints the token isn't the API's own client. RocketWiki.Api's
    `Keycloak:Audience` config must match this string (`appsettings.json`).
- Two realm roles (`admin`, `user`) and a `default-roles-rocketwiki` composite
  granting `user` to every account, matching design.md §6.5's instance-role
  model — `admin` here is a Keycloak role, not an ABAC bypass; RocketWiki
  deliberately never lets it skip page restrictions.
- Two groups (`engineering`, `export-cleared`) and seven dev users, picked to
  exercise the rule engine's and the eyes-only caveat's edge cases
  (design.md §6, §21) rather than just the happy path: all five caveat
  nationalities, a dual national, the absent-claim fail-closed case, and an
  admin who holds no grant. `nationality` is the **only** per-user attribute.
  There is deliberately no `clearance` and no per-selector attribute: the
  classification level on a marking is display-only and is never compared
  against a person, and a selector is conferred solely by a space's access
  grant — so nothing about a user in Keycloak says what they are cleared
  for or eligible for, and the API reads no such claim.

  | Username | Groups | `nationality` | Why |
  |---|---|---|---|
  | `alice.engineer` | engineering | `["NZ"]` | baseline allow: an `engineering` grant resolves, and the `NZ` caveat token |
  | `bob.engineer` | engineering | `["US","CAN"]` | dual national covering `CAN`; also the user to hand a `BANANA` grant to when proving that the grant alone confers the selector — there is no token attribute left that could gate it |
  | `carol.noattr` | engineering | *(none)* | **fail-closed case (§6.3, §21.4)**: no `nationality` claim matches no `attr` condition and fails every caveat, however the group resolves |
  | `dave.dual` | engineering | `["NZ","UK"]` | multivalued claim spelled with the fixed `UK` token (was `GB`); also exercises whether `in` matches *any* held nationality (design.md §17) |
  | `erin.export` | export-cleared (not engineering) | `["US"]` | satisfies an `anyOf` group branch without satisfying an `allOf` engineering branch |
  | `grace.aus` | engineering | `["AUS"]` | covers the `AUS` caveat token, which nobody else holds |
  | `frank.admin` | *(none)* | *(none)* | realm roles `admin` + `user`, no groups, no attributes — proves an admin with no matching grant still fails ABAC checks (§6.5 "no bypass") and sees a page with no matching access grant as `(protected)` like anyone else |

  All seven share the password `RocketWiki!Dev1`, chosen for one place to
  look it up, not for actual users to reuse. **Never use this realm, these
  users, or this password outside local development.**

## The client-scope trap

**Do not move these mappers back onto a realm-level `clientScopes` entry.**

The original version of this file declared a `rocketwiki-claims` client scope
at realm level and listed it in `rocketwiki-web`'s `defaultClientScopes`
alongside Keycloak's built-ins (`acr`, `basic`, `email`, `profile`, `roles`,
`web-origins`). That is valid JSON, imports without a single warning, and is
broken.

Supplying a top-level `clientScopes` array makes it **the realm's complete set
of client scopes** — Keycloak then does not create its built-ins at all. The
imported realm ends up holding only `offline_access` and `rocketwiki-claims`,
so six of the seven names in `defaultClientScopes` refer to scopes that do not
exist and are silently dropped. Confirmed against the running container:
`GET /admin/realms/rocketwiki/client-scopes` returned exactly those two, while
the untouched `master` realm had the full built-in set.

The consequence is worse than losing `profile`/`email`: since Keycloak 24 the
**`sub` claim lives in the `basic` scope**, so access tokens came back with no
`sub` at all. `PrincipalBuilder.Build` returns `null` without one, which means
*every* request from a perfectly valid login was treated as unauthenticated —
and `preferred_username`/`name`/`email` were missing too, which JIT
provisioning uses for the display name. Declaring the mappers on the client
leaves Keycloak's own scope bootstrap alone and the problem disappears.

## What has been verified

As of 2026-08-28 this realm has actually been imported and exercised against
`quay.io/keycloak/keycloak:26.6.1` under `aspire run`, not merely `JSON.parse`d:
every dev user completes an Authorization Code + PKCE flow, and the decoded
access tokens carry `sub`, `preferred_username`, `email`, `name`,
`aud: rocketwiki-api`, `groups` as bare names (`["engineering"]`, not
`["/engineering"]`), `nationality` as an array including the dual-national
`["NZ","GB"]`, a `clearance` claim (which this realm has since stopped
emitting — see below), and `roles` (`["admin","user"]` for `frank.admin`).
`carol.noattr` carried no `nationality` or `clearance` claim at all, which is
the §6.3 fail-closed case behaving as designed. The tokens were then put
through the real API: authentication, JIT provisioning, an ABAC grant
resolving from the `engineering` group, and the instance-admin gate accepting
`frank.admin` while refusing `alice.engineer`.

Changed since that run and **unverified until the next `aspire run`**:

- **2026-09-04 — the `clearance` and `fruit` mappers and every `clearance` /
  `fruit` user attribute were removed.** The classification gate and the
  selector-eligibility gate left the engine the same day (design.md §21):
  the classification level on a marking is display-only, and a selector is
  conferred by a space's access grant alone, so the API no longer reads
  either claim. The JSON parses, and what remains (`groups`, `nationality`,
  `roles`, the audience mapper, every user's `nationality` and group
  memberships) is byte-for-byte what the verified run imported — but that
  is all that can be said from here. Note the re-import rule: `--import-realm`
  only imports into a **fresh** volume, so a developer who keeps their
  `keycloak-data` volume across this change keeps minting tokens that carry
  the old claims (harmlessly — nothing reads them) until the volume is
  reset. The first login after a reset is what proves the claims are gone.
- Still outstanding from the previous change: the `grace.aus` user,
  `bob.engineer`'s second nationality (`CAN`), and `dave.dual`'s `GB`→`UK`
  respelling.

Still version-sensitive, so re-validate if the Keycloak tag moves: the
`protocolMapper` type names (`oidc-group-membership-mapper`,
`oidc-usermodel-attribute-mapper`, `oidc-usermodel-realm-role-mapper`,
`oidc-audience-mapper`), and which built-in scope carries `sub` — that moved
once already, in 24.

## What a production Keycloak needs

Production Keycloak is an **existing external service**, not a container
Aspire runs (design.md §15) — this file is never imported into it. Whoever
owns that realm needs to reproduce the same three things by hand (or via
Keycloak's own export/import, or Terraform/the Keycloak admin API):

1. **The same protocol mappers**, on whatever client(s) the production SPA
   and API actually use — `groups` (full path off), `nationality`
   (multivalued), `roles` (flat), and an audience mapper naming the
   production API client. Without these, the token never carries the claims
   RocketWiki's rule engine and eyes-only caveat evaluate, and every rule or
   caveat referencing them fails closed (matches nobody).
2. **A `nationality` user attribute** populated for every user who needs the
   eyes-only caveat (design.md §21.4) or an `attr` rule to resolve —
   populated in Keycloak itself, since design.md §6.2 makes Keycloak the
   single source of truth for these values. **Values must be drawn from the
   fixed five tokens `AUS`, `CAN`, `NZ`, `UK`, `US`**, because the caveat
   compares against exactly those and reads no registry: any other spelling
   (`GB`, `USA`, lower case) is ignored, so a user carrying only such a value
   fails every caveated page — fail closed, and visible at first login rather
   than representable as a working state. Dual nationals need the multivalued
   mapper config, same as here. If a `nationality` row is ever created in the
   attribute registry (§6.2) so the rule builder can offer `attr` conditions
   a picklist, it should list the same five values.
3. **The realm roles `admin` and `user`** (design.md §6.5) — `user` granted
   to every account by default, `admin` assigned by hand to instance admins
   — and **groups**: `export-cleared`/`engineering`-equivalents, or whatever
   the real org's group structure is. Names only need to match what
   RocketWiki's access rules reference; RocketWiki has no opinion on Keycloak
   group hierarchy beyond that.

That is the whole list. Two things an earlier version of this file asked for
are now deliberately **absent**, and production must not add them:

- **No `clearance` attribute or mapper.** The classification level on a
  marking is display-only, like the `UK` prefix — nothing in RocketWiki
  compares it against a person, so a clearance claim would be inert.
- **No per-selector-category eligibility mapper.** Selector eligibility is
  no longer a Keycloak concern: a selector is conferred solely by a space's
  access grant (design.md §6.4, §21.15). A reader sees a page carrying
  `APPLE` only when an access grant that matches them carries `APPLE`, and
  no token claim widens or narrows that. A claim named after a selector
  category is not read by anything.

Two things this dev realm does that production must **not** silently copy:
`sslRequired: "none"` (production Keycloak must require TLS), and the shared
dev password. Neither belongs anywhere near a real deployment.
