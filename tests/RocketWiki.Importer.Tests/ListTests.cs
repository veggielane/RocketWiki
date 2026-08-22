namespace RocketWiki.Importer.Tests;

public class ListTests : ConverterTestBase
{
    [Fact]
    public void Unordered_list_converts_to_dash_markers()
    {
        var result = Convert("<ul><li><p>First</p></li><li><p>Second</p></li></ul>");

        Assert.Equal("- First\n- Second\n", result.Markdown);
    }

    [Fact]
    public void Ordered_list_always_emits_sequential_markers_even_if_source_repeats_one()
    {
        // Confluence (and content pasted from elsewhere) sometimes repeats "1." on every
        // item; design.md §4's canonical normalization says this becomes sequential on
        // first save, so the importer emits the canonical form directly.
        var result = Convert("<ol><li><p>First</p></li><li><p>Second</p></li><li><p>Third</p></li></ol>");

        Assert.Equal("1. First\n2. Second\n3. Third\n", result.Markdown);
    }

    [Fact]
    public void Nested_unordered_list_is_indented_under_its_parent_item_with_no_blank_line_before_it()
    {
        // design.md §4's canonical normalization table: a blank line before a nested
        // sub-list (a "loose" list) has no representation in the editor's document model
        // and flattens to "tight" on first save. Emitting tight directly avoids shipping
        // Markdown that would silently reformat itself the moment someone opens the page.
        var xhtml = """
            <ul>
              <li><p>Item 1</p></li>
              <li><p>Item 2</p>
                <ul>
                  <li><p>Nested A</p></li>
                  <li><p>Nested B</p></li>
                </ul>
              </li>
            </ul>
            """;

        var result = Convert(xhtml);

        Assert.Equal("- Item 1\n- Item 2\n  - Nested A\n  - Nested B\n", result.Markdown);
    }

    [Fact]
    public void Task_list_converts_to_gfm_task_items_with_completion_state()
    {
        var xhtml = """
            <ac:task-list>
              <ac:task>
                <ac:task-status>complete</ac:task-status>
                <ac:task-body>Write the converter</ac:task-body>
              </ac:task>
              <ac:task>
                <ac:task-status>incomplete</ac:task-status>
                <ac:task-body>Write the tests</ac:task-body>
              </ac:task>
            </ac:task-list>
            """;

        var result = Convert(xhtml);

        Assert.Equal("- [x] Write the converter\n- [ ] Write the tests\n", result.Markdown);
        Assert.Empty(result.Report.Issues);
    }

    [Fact]
    public void List_item_containing_a_blockquote_indents_the_quote_under_the_marker()
    {
        var xhtml = "<ul><li><p>Said:</p><blockquote><p>quoted text</p></blockquote></li></ul>";

        var result = Convert(xhtml);

        Assert.Equal("- Said:\n\n  > quoted text\n", result.Markdown);
    }
}
