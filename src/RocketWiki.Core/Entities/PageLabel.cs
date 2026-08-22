namespace RocketWiki.Core.Entities;

/// <summary>data-model.md: PageLabel — many-to-many join, composite PK (PageId, LabelId).</summary>
public class PageLabel
{
    public Guid PageId { get; set; }
    public Page? Page { get; set; }
    public Guid LabelId { get; set; }
    public Label? Label { get; set; }
}
