import { useCurrentUserQuery } from '../graphql/generated/graphql'

/**
 * Whether the signed-in user holds the instance `admin` role (design.md
 * §6.5) — read from the server-computed `CurrentUser.isInstanceAdmin`
 * flag, i.e. the server's own InstanceRoleAccessor verdict, rather than a
 * client-side decode of the roles claim that could drift from the server's
 * interpretation. Kept as a hook so call sites don't care where the answer
 * comes from. Fails closed: a query error or missing data reads as "not an
 * admin". This is UX gating only either way — every admin surface is
 * enforced server-side regardless (design.md §6.5).
 */
export function useIsInstanceAdmin(): { isInstanceAdmin: boolean; loading: boolean } {
  const [{ data, fetching }] = useCurrentUserQuery()
  return { isInstanceAdmin: data?.me.isInstanceAdmin === true, loading: fetching }
}
