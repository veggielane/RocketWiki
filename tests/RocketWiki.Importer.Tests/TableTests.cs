namespace RocketWiki.Importer.Tests;

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
    public void Colspan_places_content_in_first_cell_and_blanks_the_rest_and_is_reported_as_lossy()
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
            "| A | B | C |\n| --- | --- | --- |\n| Merged AB |  | c1 |\n",
            result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueSeverity.Lossy, issue.Severity);
        Assert.Equal(IssueCategory.LossyTransform, issue.Category);
        Assert.Contains("merged cells", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rowspan_leaves_a_blank_placeholder_in_the_spanned_row_and_is_reported_as_lossy()
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
            "| A | B |\n| --- | --- |\n| R1 | b1 |\n|  | b2 |\n",
            result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Equal(IssueCategory.LossyTransform, issue.Category);
        Assert.Contains("merged cells", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Multiple_merged_cells_in_one_table_report_only_once()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th>A</th><th>B</th><th>C</th></tr>
                <tr><td colspan="2">AB</td><td>c1</td></tr>
                <tr><td rowspan="1">a2</td><td colspan="2">BC</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Single(result.Report.Issues, i => i.Message.Contains("merged cells", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cell_alignment_style_is_dropped_and_reported()
    {
        var xhtml = """
            <table>
              <tbody>
                <tr><th style="text-align: center;">A</th><th>B</th></tr>
                <tr><td>1</td><td>2</td></tr>
              </tbody>
            </table>
            """;

        var result = Convert(xhtml);

        Assert.Equal("| A | B |\n| --- | --- |\n| 1 | 2 |\n", result.Markdown);

        var issue = Assert.Single(result.Report.Issues);
        Assert.Contains("alignment", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cell_containing_a_pipe_character_is_escaped()
    {
        var xhtml = "<table><tbody><tr><th>Expr</th></tr><tr><td>a | b</td></tr></tbody></table>";

        var result = Convert(xhtml);

        Assert.Equal("| Expr |\n| --- |\n| a \\| b |\n", result.Markdown);
    }
}
