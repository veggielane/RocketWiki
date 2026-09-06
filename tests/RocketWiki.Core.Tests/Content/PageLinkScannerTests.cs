using RocketWiki.Core.Content;
using RocketWiki.Core.Entities;
using Xunit;

namespace RocketWiki.Core.Tests.Content;

/// <summary>
/// design.md §4: page links are serialized as <c>[title](page://{id})</c>
/// (web/src/editor/marks/PageLink.ts). The scanner is the one definition of what a page
/// links to — <c>Page.linkTargets</c> and the link index both read it — so its edge cases
/// are pinned by hand here, against the shared <see cref="PageLinkCorpus"/> that the Data
/// and SQL Server tiers hold their own implementations to.
/// </summary>
public class PageLinkScannerTests
{
    public static TheoryData<string> CaseNames { get; } = new(PageLinkCorpus.Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Extract_MatchesTheCorpusAnswer(string caseName)
    {
        var testCase = PageLinkCorpus.Cases.Single(c => c.Name == caseName);

        Assert.Equal(testCase.Expected, PageLinkScanner.Extract(testCase.Content));
    }

    [Fact]
    public void Extract_NullContent_IsEmpty()
    {
        Assert.Empty(PageLinkScanner.Extract(null));
    }

    [Fact]
    public void Extract_IsDistinctInFirstOccurrenceOrder()
    {
        var content = $"{PageLinkCorpus.Link(PageLinkCorpus.B)} {PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.B)}";

        Assert.Equal([PageLinkCorpus.B, PageLinkCorpus.A], PageLinkScanner.Extract(content));
    }

    // --- PageLink.FromContent: the index definition ---------------------------------------

    [Fact]
    public void FromContent_OneRowPerDistinctTarget_WithDenseZeroBasedOrdinals()
    {
        var source = Guid.NewGuid();
        var content = $"{PageLinkCorpus.Link(PageLinkCorpus.B)} {PageLinkCorpus.Link(PageLinkCorpus.A)} {PageLinkCorpus.Link(PageLinkCorpus.B)} {PageLinkCorpus.Link(PageLinkCorpus.Dangling)}";

        var rows = PageLink.FromContent(source, content);

        Assert.Equal(
            [(PageLinkCorpus.B, 0), (PageLinkCorpus.A, 1), (PageLinkCorpus.Dangling, 2)],
            rows.Select(r => (r.TargetPageId, r.Ordinal)));
        Assert.All(rows, r => Assert.Equal(source, r.SourcePageId));
    }

    [Fact]
    public void FromContent_SelfLink_IsARow()
    {
        // The index records what the content says; whether a self-loop is drawn is the
        // graph consumer's call, not the indexer's.
        var source = Guid.NewGuid();

        var rows = PageLink.FromContent(source, PageLinkCorpus.Link(source));

        var row = Assert.Single(rows);
        Assert.Equal(source, row.TargetPageId);
        Assert.Equal(0, row.Ordinal);
    }

    [Fact]
    public void FromContent_NoLinks_IsEmpty()
    {
        Assert.Empty(PageLink.FromContent(Guid.NewGuid(), "# Nothing here"));
        Assert.Empty(PageLink.FromContent(Guid.NewGuid(), null));
    }
}
