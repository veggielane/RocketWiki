namespace RocketWiki.Core.Enums;

/// <summary>
/// design.md §6.4: what a role grant confers — space-admin ⊃ editor. <b>Neither confers
/// visibility</b>: seeing a space's data is an access grant (<c>AccessRuleKind.AccessGrant</c>),
/// and "viewer" is therefore not a role. Ordinal order matters — comparisons like
/// <c>role &gt;= SpaceRole.Editor</c> rely on the numeric values, and the values are
/// storage facts (the tinyint column and every <c>permission.change</c> audit row). The
/// value 1 was <c>Viewer</c> and is retired, not reused: the
/// <c>SplitSpaceGrantsIntoAccessAndRole</c> migration converted every such row into an
/// access grant, and the check constraint now refuses it.
/// </summary>
public enum SpaceRole : byte
{
    Editor = 2,
    SpaceAdmin = 3,
}
