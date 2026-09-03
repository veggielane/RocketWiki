namespace RocketWiki.Core.Access;

/// <summary>
/// design.md §6.4: the positive outcome of the space-access gate (S) — the principal
/// matches at least one <b>access grant</b> in the space and may therefore see its data,
/// carrying the <b>union</b> of every selector value those matching grants confer
/// (§21.15). Produced by <c>EffectivePermissionCalculator.ComputeSpaceAccess</c>; a null
/// in its place means no access grant matched, which denies everything in the space.
///
/// <para>Deliberately a distinct type from <c>SpaceRole</c>: a role (Editor, Space-admin)
/// says what a principal may <i>do</i> and confers no visibility; access says what they
/// may <i>see</i>. Roles never supersede access — a Space-admin with no matching access
/// grant manages a space whose pages they cannot read (§6.5.2). Keeping the two answers
/// in two types is what stops a call site meaning "may see" from accidentally testing
/// "holds a role", which is exactly the conflation the split exists to end.</para>
/// </summary>
/// <param name="GrantedSelectors">The union over matching access grants, canonical. Empty
/// is ordinary — most grants confer no selector — and means any selector-bearing page in
/// the space fails G for this principal.</param>
public sealed record SpaceAccess(IReadOnlySet<SelectorValue> GrantedSelectors)
{
    /// <summary>Access with no selector values — what a plain access grant confers.</summary>
    public static SpaceAccess WithoutSelectors { get; } = new(new HashSet<SelectorValue>());
}
