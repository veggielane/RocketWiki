using RocketWiki.Core.Search;
using Xunit;

namespace RocketWiki.Core.Tests.Search;

public class MarkdownHeadingsTests
{
    [Fact]
    public void Extract_AtxHeadings_WithLevelsTextAndOffsets()
    {
        const string markdown = "# Intro\n\nSome text.\n\n## Setup\n\nMore text.";

        var headings = MarkdownHeadings.Extract(markdown);

        Assert.Equal(2, headings.Count);
        Assert.Equal(new MarkdownHeading(1, "Intro", 0), headings[0]);
        Assert.Equal(2, headings[1].Level);
        Assert.Equal("Setup", headings[1].Text);
        Assert.Equal(markdown.IndexOf("## Setup", StringComparison.Ordinal), headings[1].Offset);
    }

    [Fact]
    public void Extract_SkipsHashLinesInsideFencedCodeBlocks()
    {
        const string markdown = "# Real\n```bash\n# not a heading, a comment\n```\n## AlsoReal";

        var headings = MarkdownHeadings.Extract(markdown);

        Assert.Equal(["Real", "AlsoReal"], headings.Select(h => h.Text));
    }

    [Fact]
    public void Extract_TildeFences_AlsoSkipped()
    {
        const string markdown = "~~~\n# hidden\n~~~\n# visible";

        var headings = MarkdownHeadings.Extract(markdown);

        Assert.Equal(["visible"], headings.Select(h => h.Text));
    }

    [Fact]
    public void Extract_SevenHashes_IsNotAHeading()
    {
        Assert.Empty(MarkdownHeadings.Extract("####### too deep"));
    }

    [Fact]
    public void Extract_HashesWithoutSpace_IsNotAHeading()
    {
        // CommonMark: "#5 bolt" is a paragraph, not a heading.
        Assert.Empty(MarkdownHeadings.Extract("#5 bolt"));
    }

    [Fact]
    public void Extract_ClosingHashSequence_IsStripped()
    {
        var headings = MarkdownHeadings.Extract("## Title ##");

        Assert.Equal("Title", Assert.Single(headings).Text);
    }

    [Fact]
    public void Extract_InlineMarkup_ReducesToPlainText_MatchingWhatTipTapRenders()
    {
        // The anchor contract hangs on this: the frontend slugifies the RENDERED
        // heading's text content, so "[Setup](url)" must become "Setup" here, not
        // a slug containing the URL.
        var headings = MarkdownHeadings.Extract(
            "# **Bold** and `code`\n## A [link](https://example.com/a-b) here\n### ![alt text](img.png) image");

        Assert.Equal("Bold and code", headings[0].Text);
        Assert.Equal("A link here", headings[1].Text);
        Assert.Equal("alt text image", headings[2].Text);
    }

    [Fact]
    public void Extract_EmptyHeading_YieldsEmptyText_ForTheSectionFallback()
    {
        var headings = MarkdownHeadings.Extract("##");

        Assert.Equal(string.Empty, Assert.Single(headings).Text);
    }

    [Fact]
    public void Extract_EmptyContent_YieldsNothing()
    {
        Assert.Empty(MarkdownHeadings.Extract(string.Empty));
    }

    [Fact]
    public void Extract_CrLfContent_TextExcludesCarriageReturn_OffsetsStillMatch()
    {
        const string markdown = "# One\r\ntext\r\n## Two\r\n";

        var headings = MarkdownHeadings.Extract(markdown);

        Assert.Equal(["One", "Two"], headings.Select(h => h.Text));
        Assert.Equal(markdown.IndexOf("## Two", StringComparison.Ordinal), headings[1].Offset);
    }

    [Fact]
    public void ExtractedHeadings_FlowIntoTheAnchorAlgorithm()
    {
        // End-to-end over the two pieces the search service composes: extraction +
        // the ported anchor algorithm, on the README example from the corpus.
        var headings = MarkdownHeadings.Extract("# Intro\n## Setup\n");
        var infos = headings.Select(h => new HeadingInfo(h.Level, h.Text)).ToArray();

        Assert.Equal(["intro", "intro--setup"], HeadingAnchors.ComputeHeadingAnchors(infos));
    }
}
