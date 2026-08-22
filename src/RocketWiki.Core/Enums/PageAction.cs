namespace RocketWiki.Core.Enums;

/// <summary>data-model.md: AccessRule.Action (tinyint), set iff Kind = PageRestriction.</summary>
public enum PageAction : byte
{
    View = 1,
    Edit = 2,
}
