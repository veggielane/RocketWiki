namespace RocketWiki.Core.Search;

/// <summary>
/// One chunk of a page, ready to embed (design.md §9.2). <see cref="Text"/> is the raw
/// Markdown slice (starts at a heading line, except the preamble chunk and continuation
/// pieces of an oversized section); <see cref="EmbeddingInput"/> is what actually goes to
/// the embedding endpoint — heading breadcrumb + overlap + text — and is also what the
/// indexer content-hashes, so renaming an ancestor heading correctly re-embeds the
/// sections beneath it. <see cref="HeadingPath"/>/<see cref="AnchorId"/> are the section
/// deep-link per the §9 cross-language anchor contract (empty for content before the
/// first heading), computed by <see cref="HeadingAnchors"/> over the FULL document's
/// headings so duplicate-path disambiguation (<c>-2</c>, <c>-3</c>…) is correct.
/// </summary>
public sealed record PageChunk(
    int Index,
    string Text,
    string EmbeddingInput,
    IReadOnlyList<string> HeadingPath,
    string AnchorId);

/// <summary>
/// Tuned in characters because token counts are model-specific and this pipeline is
/// model-agnostic by design (§9.2: the model is pure config). At the usual ~4 chars per
/// token for English prose, the defaults give §9.2's "~500–1000 tokens, small overlap":
/// chunks grow to at least ~500 tokens (TargetChars) by merging small adjacent sections,
/// never exceed ~1000 (MaxChars), and continuation pieces of an oversized section carry
/// ~50 tokens (OverlapChars) of trailing context from the previous piece.
/// </summary>
public sealed record ChunkerOptions(int TargetChars = 2000, int MaxChars = 4000, int OverlapChars = 200);

/// <summary>
/// design.md §9.2: pages are chunked on Markdown heading boundaries so each section
/// embeds separately and results deep-link to the matching section. Built ON TOP of the
/// existing search primitives, not beside them: <see cref="MarkdownHeadings"/> finds the
/// boundaries (fence-aware — a <c># comment</c> inside a code block is not a boundary),
/// and <see cref="HeadingAnchors"/> (the ported cross-language contract, corpus-tested)
/// computes each chunk's breadcrumb and anchor id. A second heading scanner or slugifier
/// here would inevitably drift from the one search hits already use.
///
/// Shape guarantees, in order of application:
/// 1. The document is cut into sections at every heading line; content before the first
///    heading forms a preamble section with no heading attribution.
/// 2. A section longer than <see cref="ChunkerOptions.MaxChars"/> is split on paragraph
///    (blank-line) boundaries — hard-split mid-paragraph only as a last resort — and each
///    continuation piece keeps the section's heading path/anchor plus a small overlap of
///    the previous piece in its <em>embedding input only</em> (Text stays a clean,
///    non-overlapping slice, so offsets and display never show duplicated content).
/// 3. Adjacent small sections merge until a chunk reaches
///    <see cref="ChunkerOptions.TargetChars"/> (never exceeding MaxChars), so a page of
///    one-line sections doesn't produce confetti. A merged chunk is attributed to its
///    first section — the deep link lands at the top of the merged range.
///
/// Deterministic: same markdown in, same chunks out — the indexer's chunk hashing
/// (skip-unchanged re-embeds, design.md §9.2) depends on this.
/// </summary>
public static class MarkdownChunker
{
    public static readonly ChunkerOptions DefaultOptions = new();

    public static IReadOnlyList<PageChunk> Chunk(string markdown, ChunkerOptions? options = null)
    {
        options ??= DefaultOptions;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var headings = MarkdownHeadings.Extract(markdown);
        var infos = headings.Select(h => new HeadingInfo(h.Level, h.Text)).ToArray();
        var paths = HeadingAnchors.ComputeHeadingPaths(infos);
        var anchors = HeadingAnchors.ComputeHeadingAnchors(infos);

        // 1. Sections on heading boundaries.
        var sections = new List<Section>();
        var firstOffset = headings.Count > 0 ? headings[0].Offset : markdown.Length;
        if (!string.IsNullOrWhiteSpace(markdown[..firstOffset]))
        {
            sections.Add(new Section(markdown[..firstOffset], [], string.Empty));
        }

        for (var i = 0; i < headings.Count; i++)
        {
            var end = i + 1 < headings.Count ? headings[i + 1].Offset : markdown.Length;
            var text = markdown[headings[i].Offset..end];
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add(new Section(text, paths[i], anchors[i]));
            }
        }

        // 2. Split oversized sections; 3. pack small ones.
        var pieces = sections.SelectMany(s => SplitOversized(s, options)).ToList();
        return Pack(pieces, options);
    }

    /// <summary>A section, or a piece of one: text plus its heading attribution and the overlap its embedding input should lead with.</summary>
    private sealed record Section(string Text, IReadOnlyList<string> HeadingPath, string AnchorId, string Overlap = "");

    private static IEnumerable<Section> SplitOversized(Section section, ChunkerOptions options)
    {
        if (section.Text.Length <= options.MaxChars)
        {
            yield return section;
            yield break;
        }

        var text = section.Text;
        var pieceStart = 0;
        var previousPieceText = string.Empty;

        while (pieceStart < text.Length)
        {
            var overlap = previousPieceText.Length == 0 ? string.Empty : TailOnCharBoundary(previousPieceText, options.OverlapChars);
            var end = Math.Min(pieceStart + options.MaxChars, text.Length);

            if (end < text.Length)
            {
                // Prefer the last blank line inside the window; keep at least half a
                // window of progress so a wall of blank lines can't stall the loop.
                var lastBreak = text.LastIndexOf("\n\n", end - 1, end - pieceStart, StringComparison.Ordinal);
                if (lastBreak > pieceStart + (options.MaxChars / 2))
                {
                    end = lastBreak + 1; // keep one newline; the next piece starts at the paragraph
                }
                else if (char.IsLowSurrogate(text[end]))
                {
                    end--; // hard split: never tear a surrogate pair (same rule as SnippetBuilder)
                }
            }

            var pieceText = text[pieceStart..end];
            if (!string.IsNullOrWhiteSpace(pieceText))
            {
                yield return section with { Text = pieceText, Overlap = overlap };
                previousPieceText = pieceText;
            }

            pieceStart = end;
        }
    }

    private static string TailOnCharBoundary(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var start = text.Length - length;
        if (char.IsLowSurrogate(text[start]))
        {
            start--;
        }

        return text[start..];
    }

    private static IReadOnlyList<PageChunk> Pack(List<Section> pieces, ChunkerOptions options)
    {
        var chunks = new List<PageChunk>();
        Section? first = null;
        var accumulated = new System.Text.StringBuilder();

        void Flush()
        {
            if (first is null)
            {
                return;
            }

            var text = accumulated.ToString();
            chunks.Add(new PageChunk(
                chunks.Count,
                text,
                BuildEmbeddingInput(first.HeadingPath, first.Overlap, text),
                first.HeadingPath,
                first.AnchorId));
            first = null;
            accumulated.Clear();
        }

        foreach (var piece in pieces)
        {
            if (first is not null
                && (accumulated.Length >= options.TargetChars || accumulated.Length + piece.Text.Length > options.MaxChars))
            {
                Flush();
            }

            first ??= piece;
            accumulated.Append(piece.Text);
        }

        Flush();
        return chunks;
    }

    /// <summary>
    /// Breadcrumb first (so "Turbopumps" embeds as "Engines &gt; Turbopumps" — the
    /// section title context §9.2's per-section embedding exists to preserve), then the
    /// overlap tail from the previous piece, then the chunk text. This exact string is
    /// what the indexer hashes: an ancestor heading rename or an overlap-shifting edit
    /// upstream changes it and correctly re-embeds the chunk.
    /// </summary>
    private static string BuildEmbeddingInput(IReadOnlyList<string> headingPath, string overlap, string text)
    {
        var breadcrumb = headingPath.Count == 0 ? string.Empty : string.Join(" > ", headingPath) + "\n\n";
        var lead = overlap.Length == 0 ? string.Empty : "…" + overlap + "\n";
        return breadcrumb + lead + text;
    }
}
