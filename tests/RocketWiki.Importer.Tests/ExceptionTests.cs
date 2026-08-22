namespace RocketWiki.Importer.Tests;

public class ExceptionTests : ConverterTestBase
{
    [Fact]
    public void Malformed_xml_throws_a_named_conversion_exception_rather_than_producing_bad_output()
    {
        var ex = Assert.Throws<ConfluenceConversionException>(() => Convert("<p>unclosed"));

        Assert.Contains(DefaultContext.PageTitle, ex.Message);
    }

    [Fact]
    public void Unrecognized_named_html_entity_throws_a_named_conversion_exception()
    {
        // &foo; is not a valid XML entity and not in the converter's known-entity table,
        // so the underlying XML parse fails — that must surface clearly, not silently
        // produce an empty or truncated page.
        var ex = Assert.Throws<ConfluenceConversionException>(() => Convert("<p>weird &foo; entity</p>"));

        Assert.IsType<ConfluenceConversionException>(ex);
    }

    [Fact]
    public void Numeric_character_references_do_not_throw_because_they_are_valid_xml()
    {
        var result = Convert("<p>Caf&#233; &#x2014; done</p>");

        Assert.Equal("Café — done\n", result.Markdown);
    }
}
