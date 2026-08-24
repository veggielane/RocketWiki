using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Conversion;

public sealed partial class ConfluenceStorageConverter
{
    /// <summary>
    /// Renders an XHTML table as a design.md §4 pipe table, byte-compatible with the
    /// editor's own serializer so imported tables round-trip: GFM alignment colons on the
    /// delimiter row (canonical three-dash width), MultiMarkdown span syntax for merged
    /// cells (a colspan as immediately-adjacent pipes, a rowspan as a <c>^^</c>
    /// continuation cell at the covering cell's leftmost column), and literal
    /// <c>&lt;br&gt;</c> for in-cell line breaks. What still degrades — each with a
    /// report note: a rowspan crossing the header/body boundary is split (the header
    /// keeps the content, the covered body cells go empty), a multi-row header keeps only
    /// its first row as the header, a caption becomes a plain paragraph above the table,
    /// block content inside a cell flattens to <c>&lt;br&gt;</c>-joined text, and
    /// Confluence's per-cell alignment is normalized to Markdown's per-column alignment.
    /// </summary>
    private static string RenderTable(XElement table, RenderState state)
    {
        var rows = GetRows(table);
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var captionParagraph = RenderCaption(table, state);
        var hasHeaderRow = rows[0].Elements().Any(e => e.HasLocalName("th"));
        ReportDemotedExtraHeaderRows(rows, hasHeaderRow, state);

        var notes = new TableNotes();
        var grid = BuildGrid(rows, hasHeaderRow, state, notes);
        var width = grid.Max(r => r.Count);
        if (width == 0)
        {
            return captionParagraph ?? string.Empty;
        }

        // Pad ragged rows and fill any uncovered positions with real (space-padded)
        // empty cells, so every grid position is either an anchor or a span continuation.
        for (var r = 0; r < grid.Count; r++)
        {
            var row = grid[r];
            while (row.Count < width)
            {
                row.Add(null);
            }

            for (var c = 0; c < width; c++)
            {
                row[c] ??= new GridSlot(EmptyCell, r, c);
            }
        }

        var alignments = DeriveColumnAlignments(grid, width, hasHeaderRow, state);

        var lines = new List<string>(grid.Count + 2);
        if (hasHeaderRow)
        {
            lines.Add(RenderRowLine(grid[0], 0, width));
            lines.Add(RenderDelimiterLine(alignments));
            for (var r = 1; r < grid.Count; r++)
            {
                lines.Add(RenderRowLine(grid[r], r, width));
            }
        }
        else
        {
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Info,
                IssueCategory.LossyTransform,
                "Table had no header row (no <th> cells); a blank header was synthesized because GFM pipe tables require one.",
                state.CurrentLocation));
            lines.Add("|" + string.Concat(Enumerable.Repeat("  |", width)));
            lines.Add(RenderDelimiterLine(alignments));
            for (var r = 0; r < grid.Count; r++)
            {
                lines.Add(RenderRowLine(grid[r], r, width));
            }
        }

        var markdown = string.Join('\n', lines);
        return captionParagraph is null ? markdown : captionParagraph + "\n\n" + markdown;
    }

    private static List<XElement> GetRows(XElement table)
    {
        var rows = new List<XElement>();
        foreach (var child in table.Elements())
        {
            if (child.HasLocalName("tr"))
            {
                rows.Add(child);
            }
            else if (child.HasLocalName("thead") || child.HasLocalName("tbody") || child.HasLocalName("tfoot"))
            {
                rows.AddRange(child.Elements().Where(e => e.HasLocalName("tr")));
            }
        }

        return rows;
    }

    /// <summary>One rendered cell: its content plus the span and alignment it carried in Confluence.</summary>
    private sealed class TableCellModel
    {
        public required string Content { get; init; }

        public required ColumnAlignment Alignment { get; init; }
    }

    /// <summary>
    /// One position in the table's occupancy grid: which cell covers it and where that
    /// cell is anchored — the same model the editor serializer builds from the
    /// ProseMirror document, so both sides emit identical span syntax.
    /// </summary>
    private sealed record GridSlot(TableCellModel Cell, int AnchorRow, int AnchorCol);

    private static readonly TableCellModel EmptyCell = new() { Content = string.Empty, Alignment = ColumnAlignment.None };

    private enum ColumnAlignment
    {
        None,
        Left,
        Center,
        Right,
    }

    /// <summary>Deduplicates the once-per-table report notes triggered from per-cell code paths.</summary>
    private sealed class TableNotes
    {
        private bool _blockContentReported;
        private bool _headerBoundaryReported;

        public void BlockContentFlattened(RenderState state)
        {
            if (_blockContentReported)
            {
                return;
            }

            _blockContentReported = true;
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Lossy,
                IssueCategory.LossyTransform,
                "A table cell contained block content (a list, quote, code block, heading, or nested table); table cells hold a single line of inline content (design.md §4), so it was flattened into the cell with <br> line breaks.",
                state.CurrentLocation));
        }

        public void HeaderBoundarySpanSplit(RenderState state)
        {
            if (_headerBoundaryReported)
            {
                return;
            }

            _headerBoundaryReported = true;
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Lossy,
                IssueCategory.LossyTransform,
                "A merged table cell spanned from the header row into the body; row spans cannot cross the header/body boundary (design.md §4), so the span was split — the header cell kept the content and the body cell(s) it covered were left empty.",
                state.CurrentLocation));
        }
    }

    /// <summary>
    /// Builds the occupancy grid: each cell is anchored at its top-left position and its
    /// colspan/rowspan coverage is written into every position it spans, exactly
    /// mirroring the editor serializer's grid so continuation syntax lands in the same
    /// places. Rowspans are clamped to the table's actual row count, and a rowspan
    /// anchored in the header row is split at the header/body boundary (reported once) —
    /// its covered body positions become real empty cells so later cells in those rows
    /// keep their columns.
    /// </summary>
    private static List<List<GridSlot?>> BuildGrid(List<XElement> rows, bool hasHeaderRow, RenderState state, TableNotes notes)
    {
        var grid = new List<List<GridSlot?>>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            grid.Add([]);
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var col = 0;
            foreach (var cell in rows[r].Elements().Where(e => e.HasLocalName("td") || e.HasLocalName("th")))
            {
                while (col < grid[r].Count && grid[r][col] is not null)
                {
                    col++;
                }

                var colspan = ParseSpan(cell.Attribute("colspan"));
                var rowspan = Math.Min(ParseSpan(cell.Attribute("rowspan")), rows.Count - r);

                var splitAtHeaderBoundary = hasHeaderRow && r == 0 && rowspan > 1;
                if (splitAtHeaderBoundary)
                {
                    notes.HeaderBoundarySpanSplit(state);
                }

                var model = new TableCellModel
                {
                    Content = RenderTableCellContent(cell, state, notes),
                    Alignment = GetCellAlignment(cell),
                };

                var coveredRows = splitAtHeaderBoundary ? 1 : rowspan;
                for (var rr = r; rr < r + coveredRows; rr++)
                {
                    for (var cc = col; cc < col + colspan; cc++)
                    {
                        Place(grid[rr], cc, new GridSlot(model, r, col));
                    }
                }

                if (splitAtHeaderBoundary)
                {
                    for (var rr = r + 1; rr < r + rowspan; rr++)
                    {
                        for (var cc = col; cc < col + colspan; cc++)
                        {
                            Place(grid[rr], cc, new GridSlot(EmptyCell, rr, cc));
                        }
                    }
                }

                col += colspan;
            }
        }

        return grid;
    }

    /// <summary>First writer wins: overlapping spans in malformed input can never overwrite a placed cell.</summary>
    private static void Place(List<GridSlot?> row, int index, GridSlot slot)
    {
        while (row.Count <= index)
        {
            row.Add(null);
        }

        row[index] ??= slot;
    }

    /// <summary>
    /// Mirrors the editor serializer's row emission byte for byte: an anchored cell is
    /// <c>| content </c> (an empty cell keeps its two spaces, staying distinct from a
    /// colspan merge), a rowspan continuation at the covering cell's leftmost column is
    /// <c>| ^^ </c>, and every other covered position is a bare adjacent <c>|</c>.
    /// </summary>
    private static string RenderRowLine(List<GridSlot?> row, int r, int width)
    {
        var sb = new StringBuilder();
        for (var c = 0; c < width; c++)
        {
            var slot = row[c]!;
            if (slot.AnchorRow == r && slot.AnchorCol == c)
            {
                sb.Append("| ").Append(slot.Cell.Content).Append(' ');
            }
            else if (slot.AnchorRow < r && slot.AnchorCol == c)
            {
                sb.Append("| ^^ ");
            }
            else
            {
                sb.Append('|');
            }
        }

        return sb.Append('|').ToString();
    }

    private static string RenderDelimiterLine(ColumnAlignment[] alignments) =>
        "| " + string.Join(" | ", alignments.Select(DelimiterFor)) + " |";

    private static string DelimiterFor(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Left => ":---",
        ColumnAlignment.Center => ":---:",
        ColumnAlignment.Right => "---:",
        _ => "---",
    };

    /// <summary>
    /// Confluence records alignment per cell; Markdown has exactly one alignment per
    /// column. The rule, per column: the header cell anchored at the column wins if it
    /// declares an alignment; otherwise the most common explicit alignment among the body
    /// cells anchored there (ties go to the topmost); otherwise, for a column whose
    /// header position is covered by a spanning header cell, that cell's alignment.
    /// Cells whose own alignment differs from their anchor column's derived alignment are
    /// counted and reported once per table as an informational note — nothing is lost
    /// silently, but nothing is representable more faithfully either.
    /// </summary>
    private static ColumnAlignment[] DeriveColumnAlignments(List<List<GridSlot?>> grid, int width, bool hasHeaderRow, RenderState state)
    {
        var derived = new ColumnAlignment[width];
        for (var c = 0; c < width; c++)
        {
            derived[c] = DeriveOneColumnAlignment(grid, c, hasHeaderRow);
        }

        var disagreeing = 0;
        for (var r = 0; r < grid.Count; r++)
        {
            for (var c = 0; c < width; c++)
            {
                var slot = grid[r][c]!;
                if (slot.AnchorRow == r && slot.AnchorCol == c
                    && slot.Cell.Alignment != ColumnAlignment.None
                    && slot.Cell.Alignment != derived[c])
                {
                    disagreeing++;
                }
            }
        }

        if (disagreeing > 0)
        {
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Info,
                IssueCategory.LossyTransform,
                $"Confluence aligns table cells individually, but Markdown aligns whole columns (design.md §4). Each column's alignment was taken from its header cell (or, where the header sets none, the most common body-cell alignment); {disagreeing} cell(s) whose own alignment differed were normalized to their column's.",
                state.CurrentLocation));
        }

        return derived;
    }

    private static ColumnAlignment DeriveOneColumnAlignment(List<List<GridSlot?>> grid, int c, bool hasHeaderRow)
    {
        if (hasHeaderRow)
        {
            var headerSlot = grid[0][c]!;
            if (headerSlot.AnchorRow == 0 && headerSlot.AnchorCol == c && headerSlot.Cell.Alignment != ColumnAlignment.None)
            {
                return headerSlot.Cell.Alignment;
            }
        }

        var votes = new List<ColumnAlignment>();
        for (var r = hasHeaderRow ? 1 : 0; r < grid.Count; r++)
        {
            var slot = grid[r][c]!;
            if (slot.AnchorRow == r && slot.AnchorCol == c && slot.Cell.Alignment != ColumnAlignment.None)
            {
                votes.Add(slot.Cell.Alignment);
            }
        }

        if (votes.Count > 0)
        {
            return votes
                .GroupBy(a => a)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => votes.IndexOf(g.Key))
                .First()
                .Key;
        }

        // A column whose header position is covered by a spanning header cell inherits
        // that cell's alignment when nothing anchored in the column declares one.
        if (hasHeaderRow && grid[0][c]!.Cell.Alignment != ColumnAlignment.None)
        {
            return grid[0][c]!.Cell.Alignment;
        }

        return ColumnAlignment.None;
    }

    [GeneratedRegex(@"text-align\s*:\s*(left|center|right)", RegexOptions.IgnoreCase)]
    private static partial Regex TextAlignStyle();

    private static readonly HashSet<string> AlignableInnerBlockNames =
    [
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6",
    ];

    /// <summary>
    /// A cell's explicit alignment: a <c>text-align</c> in the cell's own <c>style</c>
    /// or a legacy <c>align</c> attribute wins; failing that, Confluence's editors often
    /// record alignment on the paragraph(s) <em>inside</em> the cell, so a single
    /// unambiguous inner-block alignment counts as the cell's (inner blocks that disagree
    /// with each other yield no alignment). Only left/center/right are recognized —
    /// anything else is styling noise with no Markdown equivalent.
    /// </summary>
    private static ColumnAlignment GetCellAlignment(XElement cell)
    {
        var own = ParseAlignmentStyle((string?)cell.Attribute("style"))
                  ?? ParseAlignmentKeyword((string?)cell.Attribute("align"));
        if (own is not null)
        {
            return own.Value;
        }

        ColumnAlignment? inner = null;
        foreach (var child in cell.Elements())
        {
            if (!AlignableInnerBlockNames.Contains(child.Name.LocalName.ToLowerInvariant()))
            {
                continue;
            }

            var alignment = ParseAlignmentStyle((string?)child.Attribute("style"));
            if (alignment is null)
            {
                continue;
            }

            if (inner is null)
            {
                inner = alignment;
            }
            else if (inner != alignment)
            {
                return ColumnAlignment.None;
            }
        }

        return inner ?? ColumnAlignment.None;
    }

    private static ColumnAlignment? ParseAlignmentStyle(string? style)
    {
        if (string.IsNullOrEmpty(style))
        {
            return null;
        }

        var match = TextAlignStyle().Match(style);
        return match.Success ? ParseAlignmentKeyword(match.Groups[1].Value) : null;
    }

    private static ColumnAlignment? ParseAlignmentKeyword(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "left" => ColumnAlignment.Left,
            "center" => ColumnAlignment.Center,
            "right" => ColumnAlignment.Right,
            _ => null,
        };

    private static int ParseSpan(XAttribute? attribute)
    {
        if (attribute is null)
        {
            return 1;
        }

        return int.TryParse(attribute.Value, out var value) && value > 0 ? value : 1;
    }

    private static string? RenderCaption(XElement table, RenderState state)
    {
        var caption = table.Elements().FirstOrDefault(e => e.HasLocalName("caption"));
        if (caption is null)
        {
            return null;
        }

        var text = RenderInlineTrimmed(caption.Nodes(), state);
        state.Report.Add(new ConversionIssue(
            IssueSeverity.Lossy,
            IssueCategory.LossyTransform,
            text.Length > 0
                ? "Table captions are outside the v1 table feature set (design.md §4); the caption text was kept as a regular paragraph above the table."
                : "Table captions are outside the v1 table feature set (design.md §4); this table's caption was empty and was dropped.",
            state.CurrentLocation));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Markdown tables have exactly one header row. When Confluence supplies more than
    /// one consecutive all-&lt;th&gt; row, only the first stays a header; the rest are
    /// emitted as ordinary body rows, reported once. (A &lt;th&gt; used as a row label in
    /// a later mixed row is normal Confluence table shape, not a multi-row header.)
    /// </summary>
    private static void ReportDemotedExtraHeaderRows(List<XElement> rows, bool hasHeaderRow, RenderState state)
    {
        if (!hasHeaderRow)
        {
            return;
        }

        var demoted = 0;
        for (var r = 1; r < rows.Count; r++)
        {
            var cells = rows[r].Elements().Where(e => e.HasLocalName("td") || e.HasLocalName("th")).ToList();
            if (cells.Count == 0 || !cells.All(e => e.HasLocalName("th")))
            {
                break;
            }

            demoted++;
        }

        if (demoted > 0)
        {
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Lossy,
                IssueCategory.LossyTransform,
                $"Table had a multi-row header ({demoted + 1} rows of header cells); Markdown tables have exactly one header row (design.md §4), so the first row was kept as the header and the other {demoted} became ordinary body row(s).",
                state.CurrentLocation));
        }
    }

    /// <summary>Cell children the inline renderer can take directly (everything Confluence
    /// puts in a simple cell). Anything else routes through block rendering.</summary>
    private static bool IsInlineCellChild(XElement element) =>
        element.Name.Namespace == ConfluenceNamespaces.Ac
            ? !element.HasLocalName("task-list") && !element.HasLocalName("layout")
            : KnownInlineLocalNames.Contains(element.Name.LocalName.ToLowerInvariant());

    private static readonly HashSet<string> StructureLossCellChildNames =
    [
        "ul", "ol", "blockquote", "pre", "table", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
    ];

    /// <summary>Cell children whose flattening genuinely loses structure — as opposed to
    /// &lt;p&gt;/&lt;div&gt;, whose <c>&lt;br&gt;</c> join is §4's defined in-cell newline.</summary>
    private static bool IsStructureLossCellChild(XElement element) =>
        element.Name.Namespace == ConfluenceNamespaces.Ac
            ? element.HasLocalName("task-list") || element.HasLocalName("layout")
            : StructureLossCellChildNames.Contains(element.Name.LocalName.ToLowerInvariant());

    /// <summary>
    /// Renders a cell to the single logical source line §4 requires. Plain inline content
    /// renders directly; paragraphs join with literal <c>&lt;br&gt;</c> (the editor
    /// serializer's own multi-paragraph behavior); genuinely block content flattens the
    /// same way with a once-per-table report note. Pipes are escaped so they cannot split
    /// the cell, and a cell whose entire content is <c>^^</c> is escaped so it cannot
    /// become a rowspan continuation marker.
    /// </summary>
    private static string RenderTableCellContent(XElement cell, RenderState state, TableNotes notes)
    {
        var wasInTableCell = state.InTableCell;
        state.InTableCell = true;
        string combined;
        try
        {
            if (cell.Elements().All(IsInlineCellChild))
            {
                combined = RenderInlineTrimmed(cell.Nodes(), state);
            }
            else
            {
                if (cell.Elements().Any(IsStructureLossCellChild))
                {
                    notes.BlockContentFlattened(state);
                }

                combined = string.Join("<br>", RenderBlocks(cell.Nodes(), state).Where(b => b.Length > 0));
            }
        }
        finally
        {
            state.InTableCell = wasInTableCell;
        }

        if (combined.Contains('\n'))
        {
            // Multi-line output (a fenced code block from a macro, a multi-item list, a
            // nested table) — flatten so the row stays one source line, and say so.
            notes.BlockContentFlattened(state);
            combined = combined.Replace("\n", "<br>");
        }

        // Matches the editor serializer's cell-pipe escape for plain text: the text
        // arrives here with literal backslashes already doubled by MarkdownText.Escape,
        // so escaping the pipe on top yields the same bytes the editor emits. Known
        // divergence: a literal backslash directly before a pipe inside an inline CODE
        // span (which Escape never touches) gets one backslash fewer than the editor
        // would emit — it still parses to the correct text, and normalizes to the
        // editor's form on first save.
        var content = combined.Replace("|", "\\|").Trim();
        return content == "^^" ? "\\^^" : content;
    }
}
