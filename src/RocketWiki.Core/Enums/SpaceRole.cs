namespace RocketWiki.Core.Enums;

/// <summary>
/// design.md §6.4: space-admin ⊃ editor ⊃ viewer. Ordinal order matters — comparisons
/// like <c>role &gt;= SpaceRole.Editor</c> rely on the numeric values below.
/// </summary>
public enum SpaceRole : byte
{
    Viewer = 1,
    Editor = 2,
    SpaceAdmin = 3,
}
