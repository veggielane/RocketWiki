using System.Text;
using System.Xml.Linq;
using RocketWiki.Importer.Conversion.Internal;

namespace RocketWiki.Importer.Conversion;

public sealed partial class ConfluenceStorageConverter
{
    /// <summary>
    /// Renders an XHTML table as a GFM pipe table. Confluence tables can have merged cells
    /// (colspan/rowspan) and per-cell alignment styling, neither of which GFM pipe tables can
    /// represent (design.md §4) — both are reported once per table and degraded to a valid,
    /// parseable grid: a colspan's content goes in its first cell with the rest left blank,
    /// and a rowspan's continuation rows get blank placeholder cells so every row keeps the
    /// same column count.
    /// </summary>
    private static string RenderTable(XElement table, RenderState state)
    {
        var rows = GetRows(table);
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var hasHeaderRow = rows[0].Elements().Any(e => e.HasLocalName("th"));
        var grid = BuildGrid(rows, state);
        if (grid.Count == 0)
        {
            return string.Empty;
        }

        var columnCount = grid.Max(r => r.Count);
        foreach (var row in grid)
        {
            while (row.Count < columnCount)
            {
                row.Add(string.Empty);
            }
        }

        if (!hasHeaderRow)
        {
            state.Report.Add(new ConversionIssue(
                IssueSeverity.Info,
                IssueCategory.LossyTransform,
                "Table had no header row (no <th> cells); a blank header was synthesized because GFM pipe tables require one.",
                state.CurrentLocation));
            grid.Insert(0, Enumerable.Repeat(string.Empty, columnCount).ToList());
        }

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", grid[0])).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columnCount))).Append(" |");
        for (var i = 1; i < grid.Count; i++)
        {
            sb.Append('\n').Append("| ").Append(string.Join(" | ", grid[i])).Append(" |");
        }

        return sb.ToString();
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

    private static List<List<string>> BuildGrid(List<XElement> rows, RenderState state)
    {
        var grid = new List<List<string>>(rows.Count);
        var pendingRowSpans = new Dictionary<int, int>();
        var mergeReported = false;
        var alignmentReported = false;

        foreach (var tr in rows)
        {
            var row = new List<string>();
            var colIndex = 0;
            var cells = tr.Elements().Where(e => e.HasLocalName("td") || e.HasLocalName("th")).ToList();

            foreach (var cell in cells)
            {
                colIndex = FillPendingRowSpans(row, pendingRowSpans, colIndex);

                var colspan = ParseSpan(cell.Attribute("colspan"));
                var rowspan = ParseSpan(cell.Attribute("rowspan"));

                if (!alignmentReported && HasAlignmentStyle(cell))
                {
                    alignmentReported = true;
                    state.Report.Add(new ConversionIssue(
                        IssueSeverity.Lossy,
                        IssueCategory.LossyTransform,
                        "Table cell alignment is not supported by GFM pipe tables in v1 (design.md §4) and was dropped.",
                        state.CurrentLocation));
                }

                if (!mergeReported && (colspan > 1 || rowspan > 1))
                {
                    mergeReported = true;
                    state.Report.Add(new ConversionIssue(
                        IssueSeverity.Lossy,
                        IssueCategory.LossyTransform,
                        "Table contains merged cells (colspan/rowspan), which GFM pipe tables cannot represent (design.md §4). The merged content was placed in the first cell of the span and the remaining spanned cells were left blank.",
                        state.CurrentLocation));
                }

                var content = RenderTableCellContent(cell, state);
                for (var c = 0; c < colspan; c++)
                {
                    row.Add(c == 0 ? content : string.Empty);
                    if (rowspan > 1)
                    {
                        pendingRowSpans[colIndex] = rowspan - 1;
                    }

                    colIndex++;
                }
            }

            FillPendingRowSpans(row, pendingRowSpans, colIndex);
            grid.Add(row);
        }

        return grid;
    }

    /// <summary>Inserts blank placeholder cells for any column still consumed by a rowspan from
    /// an earlier row, advancing <paramref name="colIndex"/> and decrementing each pending span.</summary>
    private static int FillPendingRowSpans(List<string> row, Dictionary<int, int> pendingRowSpans, int colIndex)
    {
        while (pendingRowSpans.TryGetValue(colIndex, out var remaining))
        {
            row.Add(string.Empty);
            if (remaining <= 1)
            {
                pendingRowSpans.Remove(colIndex);
            }
            else
            {
                pendingRowSpans[colIndex] = remaining - 1;
            }

            colIndex++;
        }

        return colIndex;
    }

    private static bool HasAlignmentStyle(XElement cell)
    {
        var style = (string?)cell.Attribute("style");
        if (style is not null && style.Contains("text-align", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var align = (string?)cell.Attribute("align");
        return !string.IsNullOrEmpty(align);
    }

    private static int ParseSpan(XAttribute? attribute)
    {
        if (attribute is null)
        {
            return 1;
        }

        return int.TryParse(attribute.Value, out var value) && value > 0 ? value : 1;
    }

    private static string RenderTableCellContent(XElement cell, RenderState state)
    {
        var blocks = RenderBlocks(cell.Nodes(), state);
        var combined = string.Join("<br>", blocks.Where(b => !string.IsNullOrEmpty(b)));
        return combined.Replace("|", "\\|").Replace("\n", " ").Trim();
    }
}
