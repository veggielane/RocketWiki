using RocketWiki.Core.Search;
using Xunit;

namespace RocketWiki.Core.Tests.Search;

public class SnippetBuilderTests
{
    [Fact]
    public void LocateFirstMatch_FindsEarliestTerm_CaseInsensitive()
    {
        var index = SnippetBuilder.LocateFirstMatch("Alpha COMBUSTION beta", "combustion", out var length);

        Assert.Equal(6, index);
        Assert.Equal("combustion".Length, length);
    }

    [Fact]
    public void LocateFirstMatch_MultiTermQuery_EarliestOccurrenceWinsRegardlessOfTermOrder()
    {
        var index = SnippetBuilder.LocateFirstMatch("nozzle then chamber", "chamber nozzle", out var length);

        Assert.Equal(0, index);
        Assert.Equal("nozzle".Length, length);
    }

    [Fact]
    public void LocateFirstMatch_QuotedPhraseTerms_StillLocateTheirWords()
    {
        var index = SnippetBuilder.LocateFirstMatch("the combustion chamber", "\"combustion\"", out _);

        Assert.Equal(4, index);
    }

    [Fact]
    public void LocateFirstMatch_NoMatch_ReturnsMinusOne()
    {
        Assert.Equal(-1, SnippetBuilder.LocateFirstMatch("nothing relevant", "zzz", out _));
    }

    [Fact]
    public void Build_ShortContent_ReturnsWholeContent_NoEllipsis()
    {
        Assert.Equal("short content", SnippetBuilder.Build("short content", 0, 0));
    }

    [Fact]
    public void Build_MatchDeepInLongContent_CentersWindowAndEllipsizesBothSides()
    {
        var content = new string('a', 500) + " NEEDLE " + new string('b', 500);
        var matchIndex = content.IndexOf("NEEDLE", StringComparison.Ordinal);

        var snippet = SnippetBuilder.Build(content, matchIndex, "NEEDLE".Length, maxLength: 100);

        Assert.Contains("NEEDLE", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        // 100 window chars + 2 ellipses is the ceiling; whitespace collapse can only shrink it.
        Assert.True(snippet.Length <= 102, $"snippet unexpectedly long: {snippet.Length}");
    }

    [Fact]
    public void Build_MatchAtStart_NoLeadingEllipsis()
    {
        var content = "NEEDLE " + new string('b', 500);

        var snippet = SnippetBuilder.Build(content, 0, "NEEDLE".Length, maxLength: 50);

        Assert.StartsWith("NEEDLE", snippet);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Build_MatchAtEnd_WindowReanchorsToContentEnd_NoTrailingEllipsis()
    {
        var content = new string('a', 500) + " NEEDLE";
        var matchIndex = content.IndexOf("NEEDLE", StringComparison.Ordinal);

        var snippet = SnippetBuilder.Build(content, matchIndex, "NEEDLE".Length, maxLength: 50);

        Assert.EndsWith("NEEDLE", snippet);
        Assert.StartsWith("…", snippet);
    }

    [Fact]
    public void Build_CollapsesNewlinesAndWhitespaceRuns_MarkdownSourceReadsAsOneLine()
    {
        var snippet = SnippetBuilder.Build("line one\n\n   line two\t\tend", 0, 0);

        Assert.Equal("line one line two end", snippet);
    }

    [Fact]
    public void Build_NeverSplitsASurrogatePair()
    {
        // Rocket emoji (astral plane, one surrogate pair each) on both cut boundaries.
        var content = string.Concat(Enumerable.Repeat("🚀", 200));

        for (var maxLength = 1; maxLength <= 7; maxLength++)
        {
            var snippet = SnippetBuilder.Build(content, 200, 2, maxLength);

            // A torn pair would surface as a lone surrogate char; round-tripping
            // through UTF-8 would corrupt it, so assert well-formedness directly.
            foreach (var (c, i) in snippet.Select((c, i) => (c, i)))
            {
                if (char.IsHighSurrogate(c))
                {
                    Assert.True(i + 1 < snippet.Length && char.IsLowSurrogate(snippet[i + 1]),
                        $"lone high surrogate at {i} with maxLength {maxLength}");
                }
                else if (char.IsLowSurrogate(c))
                {
                    Assert.True(i > 0 && char.IsHighSurrogate(snippet[i - 1]),
                        $"lone low surrogate at {i} with maxLength {maxLength}");
                }
            }
        }
    }

    [Fact]
    public void Build_EmptyContent_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SnippetBuilder.Build(string.Empty, 0, 0));
    }

    [Fact]
    public void Build_OutOfRangeMatchIndex_IsClampedNotThrown()
    {
        // Defensive contract: a caller passing an index from a *different* string
        // (or -1 by mistake) gets a sane excerpt, never an exception mid-search.
        Assert.Equal("abc", SnippetBuilder.Build("abc", 9999, 5));
        Assert.Equal("abc", SnippetBuilder.Build("abc", -5, 2));
    }
}
