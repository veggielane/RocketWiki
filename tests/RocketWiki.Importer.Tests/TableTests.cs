namespace RocketWiki.Importer.Tests;

/// <summary>
/// design.md §4 tables, phase 2: the converter PRESERVES merged cells (MultiMarkdown
/// adjacent-pipe colspans and <c>^^</c> rowspan continuations), per-column alignment (GFM
/// delimiter colons), and in-cell line breaks (literal <c>&lt;br&gt;</c>) instead of
/// degrading them. The emitted bytes are a cross-language contract with the editor
/// serializer (web/src/editor), enforced by the regenerated converted-markdown corpus —
/// the expected strings here deliberately match that serializer's exact conventions
/// (three-dash delimiters, space-padded cells, "|  |" real empties).
/// </summary>
public class TableTests : ConverterTestBase
{
    [Fact]
    public void Simple_table_with_header_converts_to_gfm_pipe_table()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>Name</th><th>Role</th></tr>
                <tr><td>Ada</td><td>Engineer</td></tr>
                <tr><td>Grace</td><td>Admiral</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| Name | Role |\n| --- | --- |\n| Ada | Engineer |\n| Grace | Admiral |\n",
            result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Table_without_header_row_gets_a_synthesized_blank_header_and_is_reported()
    {
        var xhtml = "<table><tbody><tr><td>x</td><td>y</td></tr><tr><td>1</td><td>2</td></tr></tbody></table>";

        var result = Convert(xhtml);

        Assert.Equal("|  |  |\n| --- | --- |\n| x | y |\n| 1 | 2 |\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Contains("no header row", issue.Message);
    }

    [Fact]
    public void Colspan_is_preserved_as_an_adjacent_pipe_merge_with_no_report_issue()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>A</th><th>B</th><th>C</th></tr>
                <tr><td colspan="2">Merged AB</td><td>c1</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| A | B | C |\n| --- | --- | --- |\n| Merged AB || c1 |\n",
            result.Markdown);
        Assert.Empty(result.Report.Issues);
        Assert.False(result.Report.HasLossyIssues);
    }

    [Fact]
    public void Rowspan_is_preserved_as_a_caret_continuation_cell_with_no_report_issue()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>A</th><th>B</th></tr>
                <tr><td rowspan="2">R1</td><td>b1</td></tr>
                <tr><td>b2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| A | B |\n| --- | --- |\n| R1 | b1 |\n| ^^ | b2 |\n",
            result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Cell_spanning_both_axes_emits_colspan_matched_continuation_cells()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>Stage</th><th>Result</th><th>Notes</th></tr>
                <tr><td colspan="2" rowspan="2">Coast</td><td>n1</td></tr>
                <tr><td>n2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| Stage | Result | Notes |\n| --- | --- | --- |\n| Coast || n1 |\n| ^^ || n2 |\n",
            result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Rowspan_exceeding_the_table_is_clamped_to_the_rows_that_exist()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>A</th><th>B</th></tr>
                <tr><td rowspan="99">R1</td><td>b1</td></tr>
                <tr><td>b2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| A | B |\n| --- | --- |\n| R1 | b1 |\n| ^^ | b2 |\n",
            result.Markdown);
    }

    [Fact]
    public void Header_cell_alignment_styles_become_gfm_delimiter_colons_with_no_report_issue()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th style="text-align: left;">L</th><th style="text-align: center;">C</th><th style="text-align: right;">R</th><th>D</th></tr>
                <tr><td>1</td><td>2</td><td>3</td><td>4</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| L | C | R | D |\n| :--- | :---: | ---: | --- |\n| 1 | 2 | 3 | 4 |\n",
            result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Alignment_recorded_on_the_paragraph_inside_the_cell_counts_as_the_cells_alignment()
    {
        // Confluence's editors often put text-align on the <p> inside the cell rather
        // than on the <th>/<td> itself.
        var xhtml = """
            <table>
              <tbody>
                <tr><th><p style="text-align: center;">C</p></th><th>D</th></tr>
                <tr><td><p style="text-align: center;">1</p></td><td>2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| C | D |\n| :---: | --- |\n| 1 | 2 |\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Legacy_align_attribute_is_recognized_too()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th align="right">R</th><th>D</th></tr>
                <tr><td>1</td><td>2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| R | D |\n| ---: | --- |\n| 1 | 2 |\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Column_alignment_falls_back_to_the_body_majority_when_the_header_sets_none()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>A</th></tr>
                <tr><td style="text-align: right;">1</td></tr>
                <tr><td style="text-align: right;">2</td></tr>
                <tr><td style="text-align: center;">3</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| A |\n| ---: |\n| 1 |\n| 2 |\n| 3 |\n", result.Markdown);

        // The center-aligned minority cell was normalized to the column's alignment —
        // reported once, informationally: nothing a reader would call content was lost.
        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Contains("1 cell(s)", issue.Message);
        Assert.False(result.Report.HasLossyIssues);
    }

    [Fact]
    public void Header_cell_alignment_wins_over_a_disagreeing_body_majority()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th style="text-align: center;">A</th></tr>
                <tr><td style="text-align: right;">1</td></tr>
                <tr><td style="text-align: right;">2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| A |\n| :---: |\n| 1 |\n| 2 |\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Contains("2 cell(s)", issue.Message);
    }

    [Fact]
    public void Rowspan_crossing_the_header_body_boundary_is_split_and_reported_as_lossy()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th rowspan="2">Metric</th><th>Q1</th></tr>
                <tr><td>42</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        // The header keeps the content; the covered body cell becomes a REAL empty
        // (spaces between pipes) so it can never read as a merge — and the body cell
        // "42" stays in its own column.
        Assert.Equal("| Metric | Q1 |\n| --- | --- |\n|  | 42 |\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Equal(IssueCategory.LossyTransform, issue.Category);
        Assert.Contains("header/body boundary", issue.Message);
        Assert.True(result.Report.HasLossyIssues);
    }

    [Fact]
    public void Multiple_header_boundary_spans_in_one_table_report_only_once()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th rowspan="2">A</th><th rowspan="2">B</th></tr>
                <tr></tr>
                <tr><td>1</td><td>2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Single(result.Report.Issues, i => i.Message.Contains("header/body boundary"));
    }

    [Fact]
    public void Multi_row_header_keeps_only_the_first_row_as_header_and_is_reported()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>A</th><th>B</th></tr>
                <tr><th>a2</th><th>b2</th></tr>
                <tr><td>1</td><td>2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal(
            "| A | B |\n| --- | --- |\n| a2 | b2 |\n| 1 | 2 |\n",
            result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Contains("multi-row header", issue.Message);
    }

    [Fact]
    public void Row_label_th_cells_in_mixed_body_rows_are_not_a_multi_row_header()
    {
        // The common Confluence "sideways header" shape: a <th> label at the start of
        // each body row. Not a multi-row header — no note.
        var xhtml = """
            <table>
              <tbody>
                <tr><th>Key</th><th>Value</th></tr>
                <tr><th>host</th><td>db01</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| Key | Value |\n| --- | --- |\n| host | db01 |\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Caption_is_flattened_to_a_paragraph_above_the_table_and_reported()
    {
        var xhtml = """
            <table>
              <caption>Launch windows</caption>
              <tbody>
                <tr><th>A</th></tr>
                <tr><td>1</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("Launch windows\n\n| A |\n| --- |\n| 1 |\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Contains("caption", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Br_inside_a_cell_becomes_a_literal_lowercase_br_tag()
    {
        var xhtml = "<table><tbody><tr><th>H</th></tr><tr><td>go<br/>go</td></tr></tbody></table>";

        var result = Convert(xhtml);

        Assert.Equal("| H |\n| --- |\n| go<br>go |\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Multiple_paragraphs_in_a_cell_join_with_br_and_are_not_a_report_issue()
    {
        var xhtml = "<table><tbody><tr><th>H</th></tr><tr><td><p>first</p><p>second</p></td></tr></tbody></table>";

        var result = Convert(xhtml);

        Assert.Equal("| H |\n| --- |\n| first<br>second |\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void Br_outside_a_table_still_renders_as_a_gfm_hard_break()
    {
        var result = Convert("<p>a<br/>b</p>");

        Assert.Equal("a  \nb\n", result.Markdown);
    }

    [Fact]
    public void Cell_containing_a_pipe_character_is_escaped()
    {
        var xhtml = "<table><tbody><tr><th>Expr</th></tr><tr><td>a | b</td></tr></tbody></table>";

        var result = Convert(xhtml);

        Assert.Equal("| Expr |\n| --- |\n| a \\| b |\n", result.Markdown);
    }

    [Fact]
    public void Cell_whose_entire_content_is_two_carets_is_escaped_so_it_is_not_a_rowspan_marker()
    {
        var xhtml = "<table><tbody><tr><th>M</th><th>N</th></tr><tr><td>^^</td><td>^^ but longer</td></tr></tbody></table>";

        var result = Convert(xhtml);

        // Only an exact-^^ cell needs the escape; ^^ as part of longer text does not.
        Assert.Equal("| M | N |\n| --- | --- |\n| \\^^ | ^^ but longer |\n", result.Markdown);
    }

    [Fact]
    public void Block_content_in_a_cell_is_flattened_to_br_joined_text_and_reported_once_per_table()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>H</th><th>I</th></tr>
                <tr><td><ul><li><p>one</p></li><li><p>two</p></li></ul></td><td><ol><li><p>x</p></li></ol></td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| H | I |\n| --- | --- |\n| - one<br>- two | 1. x |\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Contains("block content", issue.Message);
    }

    [Fact]
    public void Inline_formatting_and_mixed_inline_content_in_cells_render_as_one_line()
    {
        var xhtml = "<table><tbody><tr><th>Cmd</th></tr><tr><td>run <code>deploy</code> with <strong>care</strong></td></tr></tbody></table>";

        var result = Convert(xhtml);

        Assert.Equal("| Cmd |\n| --- |\n| run `deploy` with **care** |\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }
}
