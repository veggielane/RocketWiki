import { useAuth } from 'react-oidc-context'

/**
 * Whether the signed-in user holds Keycloak's realm-level "admin" role
 * (design.md §6.5) — read from the ID token's flat `roles` claim, which the
 * dev realm's dedicated protocol mapper puts in the ID token as well as the
 * access token (rocketwiki-realm.json), exactly mirroring the server's
 * InstanceRoleAccessor (`FindAll("roles").Any(v => v == "admin")`).
 *
 * NOTE (schema reconciliation): this used to be the server-computed
 * `CurrentUser.isInstanceAdmin`, but the real schema's CurrentUser carries
 * no such flag — reported as a contract gap; a server-computed field is
 * preferable to a client-side claim decode, which can drift from the
 * server's interpretation. This is UX gating only either way: every admin
 * surface is enforced server-side regardless (design.md §6.5).
 */
export function useIsInstanceAdmin(): { isInstanceAdmin: boolean; loading: boolean } {
  const auth = useAuth()
  const roles = auth.user?.profile['roles']
  const isInstanceAdmin = Array.isArray(roles) && roles.includes('admin')
  return { isInstanceAdmin, loading: auth.isLoading }
}
