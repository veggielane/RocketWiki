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
- Protocol mappers **on the `rocketwiki-web` client itself**, not on a shared
  client scope. That placement is load-bearing and was learned the hard way —
  see "The client-scope trap" below. They are what makes the ABAC engine
  possible (design.md §6, §11 step 5):
  - **`groups`** (`oidc-group-membership-mapper`, `full.path: false`) — group
    *names*, not paths, so they match rule expressions like
    `{"group": "engineering"}` directly.
  - **`nationality`** (`oidc-usermodel-attribute-mapper`, `multivalued: true`)
    — always emitted as a JSON array, per data-model.md's `string[]` typing
    for dual nationals.
  - **`clearance`** (`oidc-usermodel-attribute-mapper`, `multivalued: false`)
    — the protective-marking clearance (design.md §21). Single-valued on
    purpose; `ClearanceGate` still reads it as a list and takes the highest
    recognised value, so a scalar claim is the one-element case.
  - **`fruit`** (`oidc-usermodel-attribute-mapper`, `multivalued: false`) —
    the eligibility claim for the dev instance's `FRUIT` selector category
    (design.md §21.15). The API learns which claim to read from
    `ProtectiveMarking:SelectorCategories[0].ClaimName`, wired in `AppHost.cs`
    beside the `REGION` category, which has no claim and therefore needs no
    mapper. A user is eligible only when the value is `yes`; any other value,
    or no attribute at all, means not eligible — an explicit `no` and a
    missing attribute are deliberately the same answer, which `erin.export`
    and `bob.engineer` below exercise respectively. `KeycloakClaimParityTests`
    reads every `ClaimName` the AppHost's `WithEnvironment` block configures
    and fails unless this realm has a mapper emitting each one (and that none
    of them is a reserved name), so adding a gated category to the dev catalog
    means adding its mapper here in the same change.
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
  exercise the rule engine's and the protective-marking gates' edge cases
  (design.md §6, §21) rather than just the happy path. Together they cover
  the truth table the marking overhaul was agreed against: every clearance
  tier including the absent-claim floor, every eligibility answer for
  `FRUIT` (`yes`, `no`, absent), and all five caveat nationalities.

  | Username | Groups | `nationality` | `clearance` | `fruit` | Why |
  |---|---|---|---|---|---|
  | `alice.engineer` | engineering | `["NZ"]` | `OFFICIAL` | `yes` | baseline allow; an explicit `OFFICIAL` sits *below* the absent-clearance floor, so on classification alone she reads less than `carol.noattr` |
  | `bob.engineer` | engineering | `["US","CAN"]` | `SECRET` | *(none)* | not eligible for `FRUIT` by absence — a space grant of `BANANA` alone must not let him read a `BANANA` page; dual national covering `CAN` |
  | `carol.noattr` | engineering | *(none)* | *(none)* | *(none)* | **fail-closed case (§6.3, §21.3)**: no `nationality` claim matches no `attr` condition and fails every caveat; no `clearance` claim resolves to the `OFFICIAL_SENSITIVE` floor — not to nothing, and not to everything |
  | `dave.dual` | engineering | `["NZ","UK"]` | `OFFICIAL_SENSITIVE` | `yes` | multivalued claim, now spelled with the fixed `UK` token (was `GB`); an explicit clearance equal to the floor; also exercises whether `in` matches *any* held nationality (design.md §17) |
  | `erin.export` | export-cleared (not engineering) | `["US"]` | `TOP_SECRET` | `no` | an explicit non-`yes` is **not** eligible, however high the clearance; satisfies an `anyOf` group branch without satisfying an `allOf` engineering branch |
  | `grace.aus` | engineering | `["AUS"]` | `SECRET` | `yes` | covers the `AUS` caveat token, which nobody else holds |
  | `frank.admin` | *(none)* | *(none)* | *(none)* | *(none)* | realm role `admin`, no groups, no attributes — proves an admin with no matching grant still fails ABAC checks (§6.5 "no bypass") and sees a page he holds no access grant for as `(protected)` like anyone else |

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
`["NZ","GB"]`, `clearance`, and `roles` (`["admin","user"]` for
`frank.admin`). `carol.noattr` carries no `nationality` or `clearance` claim at
all, which is the §6.3 fail-closed case behaving as designed. The tokens were
then put through the real API: authentication, JIT provisioning, an ABAC grant
resolving from the `engineering` group, and the instance-admin gate accepting
`frank.admin` while refusing `alice.engineer`.

Changed since that run and **unverified until the next `aspire run`**: the
`fruit` mapper and every `fruit` attribute value, the new `grace.aus` user,
`bob.engineer`'s second nationality (`CAN`), and `dave.dual`'s `GB`→`UK`
respelling. The JSON parses and the new mapper copies the verified
`clearance` mapper's shape field for field, which is all that can be said
from here; the first login after the next start is what proves the claim
lands in the token.

Still version-sensitive, so re-validate if the Keycloak tag moves: the
`protocolMapper` type names (`oidc-group-membership-mapper`,
`oidc-usermodel-attribute-mapper`, `oidc-usermodel-realm-role-mapper`,
`oidc-audience-mapper`), and which built-in scope carries `sub` — that moved
once already, in 24.

## What a production Keycloak needs

Production Keycloak is an **existing external service**, not a container
Aspire runs (design.md §15) — this file is never imported into it. Whoever
owns that realm needs to reproduce the same four things by hand (or via
Keycloak's own export/import, or Terraform/the Keycloak admin API):

1. **The same protocol mappers**, on whatever client(s) the production SPA
   and API actually use — `groups` (full path off), `nationality`
   (multivalued), `clearance` (single-valued), `roles` (flat), and an
   audience mapper naming the production API client. Without these, the
   token never carries the claims RocketWiki's rule engine and marking gates
   evaluate, and every rule or marking referencing them fails closed
   (matches nobody).
2. **`nationality` and `clearance` user attributes** populated for every user
   who needs the national caveat (design.md §21.4), the classification gate
   (§21.3) or an `attr` rule to resolve — populated in Keycloak itself, since
   design.md §6.2 makes Keycloak the single source of truth for these values.
   **`nationality` values must be drawn from the fixed five tokens `AUS`,
   `CAN`, `NZ`, `UK`, `US`.** The eyes-only caveat no longer reads the
   attribute registry: any other spelling (`GB`, `USA`, lower case) is
   ignored by the caveat gate, so a user carrying only such a value fails
   every caveated page — fail closed, and visible at first login rather than
   representable as a working state. Dual nationals need the multivalued
   mapper config, same as here. If a `nationality` row is ever created in the
   attribute registry (§6.2) so the rule builder can offer `attr` conditions
   a picklist, it should list the same five values.
3. **One single-valued string mapper per selector category's `ClaimName`**
   (`ProtectiveMarking:SelectorCategories[n].ClaimName` —
   docs/CONFIGURATION.md "Protective markings", design.md §21.15), emitting
   exactly `yes` for the users who are eligible for that category. Any other
   value, or no attribute, means not eligible; there is no "true"/"1"
   spelling to get wrong, and a category configured with an empty
   `ClaimName` needs no mapper at all because everyone is eligible for it.
   The dev realm's `fruit` mapper above is the template.
4. **`groups` and `export-cleared`/`engineering`-equivalent groups**, or
   whatever the real org's group structure is — names only need to match what
   RocketWiki's access rules reference; RocketWiki has no opinion on Keycloak
   group hierarchy beyond that.

Two things this dev realm does that production must **not** silently copy:
`sslRequired: "none"` (production Keycloak must require TLS), and the shared
dev password. Neither belongs anywhere near a real deployment.
