using RocketWiki.Core.Search;
using Xunit;

namespace RocketWiki.Core.Tests.Search;

/// <summary>
/// design.md §9.2: chunking on heading boundaries with heading-path + anchor
/// attribution. The chunker reuses <see cref="MarkdownHeadings"/> and
/// <see cref="HeadingAnchors"/> (the corpus-tested cross-language contract) — the
/// consistency tests here pin THAT reuse: a chunk's anchor must be byte-identical to
/// what the anchor algorithm computes for the same document, or every semantic deep
/// link would silently miss.
/// </summary>
public class MarkdownChunkerTests
{
    /// <summary>Target=1 forces a flush after every section: chunk boundaries == section boundaries.</summary>
    private static readonly ChunkerOptions PerSection = new(TargetChars: 1, MaxChars: 4000, OverlapChars: 20);

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  \t")]
    public void EmptyOrWhitespace_YieldsNoChunks(string markdown) =>
        Assert.Empty(MarkdownChunker.Chunk(markdown));

    [Fact]
    public void SplitsOnHeadingBoundaries_AndTextsReassembleTheDocument()
    {
        const string markdown = "intro before any heading\n\n# Engines\n\nabout engines\n\n## Turbopumps\n\nabout pumps\n\n# Tanks\n\nabout tanks\n";

        var chunks = MarkdownChunker.Chunk(markdown, PerSection);

        Assert.Equal(4, chunks.Count); // preamble + Engines + Turbopumps + Tanks
        Assert.Equal(Enumerable.Range(0, 4), chunks.Select(c => c.Index));
        // No content lost, none duplicated: Text pieces are clean slices.
        Assert.Equal(markdown, string.Concat(chunks.Select(c => c.Text)));
        // Every non-preamble chunk starts at its heading line.
        Assert.StartsWith("# Engines", chunks[1].Text, StringComparison.Ordinal);
        Assert.StartsWith("## Turbopumps", chunks[2].Text, StringComparison.Ordinal);
        Assert.StartsWith("# Tanks", chunks[3].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkAttribution_MatchesTheAnchorContractExactly()
    {
        const string markdown = "# Engines\n\nx\n\n## Turbopumps\n\ny\n\n## Turbopumps\n\nduplicate path\n\n# Tanks\n\nz\n";

        var chunks = MarkdownChunker.Chunk(markdown, PerSection);

        var infos = MarkdownHeadings.Extract(markdown).Select(h => new HeadingInfo(h.Level, h.Text)).ToArray();
        var expectedPaths = HeadingAnchors.ComputeHeadingPaths(infos);
        var expectedAnchors = HeadingAnchors.ComputeHeadingAnchors(infos);

        Assert.Equal(4, chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(expectedPaths[i], chunks[i].HeadingPath);
            Assert.Equal(expectedAnchors[i], chunks[i].AnchorId);
        }

        // The duplicate-path ordinal (§9's classic silent-breakage case) flows through.
        Assert.Equal("engines--turbopumps", chunks[1].AnchorId);
        Assert.Equal("engines--turbopumps-2", chunks[2].AnchorId);
    }

    [Fact]
    public void PreambleChunk_HasNoHeadingAttribution()
    {
        var chunks = MarkdownChunker.Chunk("before\n\n# First\n\nbody", PerSection);

        Assert.Empty(chunks[0].HeadingPath);
        Assert.Equal(string.Empty, chunks[0].AnchorId);
        Assert.Equal(["First"], chunks[1].HeadingPath);
    }

    [Fact]
    public void HeadingInsideFencedCodeBlock_IsNotABoundary()
    {
        const string markdown = "# Real\n\n```\n# not a heading\n```\n\ntail\n\n# AlsoReal\n\nbody";

        var chunks = MarkdownChunker.Chunk(markdown, PerSection);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("# not a heading", chunks[0].Text, StringComparison.Ordinal);
        Assert.Equal(["AlsoReal"], chunks[1].HeadingPath);
    }

    [Fact]
    public void SmallSections_MergeUpToTarget_AttributedToTheFirst()
    {
        const string markdown = "# A\n\none\n\n## B\n\ntwo\n\n## C\n\nthree";

        // Default options: this whole document is far below TargetChars, so it packs
        // into a single chunk deep-linking to the top of the merged range.
        var chunks = MarkdownChunker.Chunk(markdown);

        var only = Assert.Single(chunks);
        Assert.Equal(markdown, only.Text);
        Assert.Equal(["A"], only.HeadingPath);
        Assert.Equal("a", only.AnchorId);
    }

    [Fact]
    public void OversizedSection_SplitsOnParagraphs_WithOverlapInEmbeddingInputOnly()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"paragraph {i} " + new string('x', 90)));
        var markdown = "# Big\n\n" + paragraphs;
        var options = new ChunkerOptions(TargetChars: 1, MaxChars: 400, OverlapChars: 30);

        var chunks = MarkdownChunker.Chunk(markdown, options);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= options.MaxChars, $"chunk {c.Index} is {c.Text.Length} chars"));
        // Clean, non-overlapping Text slices - display/deep-link math never sees duplicates.
        Assert.Equal(markdown, string.Concat(chunks.Select(c => c.Text)));
        // Every piece keeps the section's attribution.
        Assert.All(chunks, c => Assert.Equal(["Big"], c.HeadingPath));
        Assert.All(chunks, c => Assert.Equal("big", c.AnchorId));

        // Continuation pieces embed with the tail of the previous piece (small overlap,
        // §9.2) plus the breadcrumb; the first piece has no overlap.
        Assert.StartsWith("Big\n\n", chunks[0].EmbeddingInput, StringComparison.Ordinal);
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousTail = chunks[i - 1].Text[^options.OverlapChars..];
            Assert.Contains("…" + previousTail, chunks[i].EmbeddingInput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmbeddingInput_LeadsWithTheBreadcrumb()
    {
        var chunks = MarkdownChunker.Chunk("# Engines\n\nx\n\n## Turbopumps\n\nimpeller margins", PerSection);

        Assert.StartsWith("Engines > Turbopumps\n\n## Turbopumps", chunks[1].EmbeddingInput, StringComparison.Ordinal);
        // Preamble-less attribution: the chunk under a heading embeds its own text too.
        Assert.EndsWith("impeller margins", chunks[1].EmbeddingInput, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunking_IsDeterministic()
    {
        var markdown = "# A\n\n" + string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"p{i} " + new string('y', 120))) + "\n\n# B\n\ntail";

        var first = MarkdownChunker.Chunk(markdown);
        var second = MarkdownChunker.Chunk(markdown);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Index, second[i].Index);
            Assert.Equal(first[i].Text, second[i].Text);
            Assert.Equal(first[i].EmbeddingInput, second[i].EmbeddingInput);
            Assert.Equal(first[i].AnchorId, second[i].AnchorId);
            Assert.Equal(first[i].HeadingPath, second[i].HeadingPath);
        }
    }
}
