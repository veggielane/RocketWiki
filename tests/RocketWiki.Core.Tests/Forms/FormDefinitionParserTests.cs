using RocketWiki.Core.Forms;
using Xunit;

namespace RocketWiki.Core.Tests.Forms;

/// <summary>
/// The form grammar. A pure function over text, which is the point: the server is the
/// authority on what a form is, and rendering and validation are both served from this
/// one parser rather than from two opinions that agree until they do not.
/// </summary>
public class FormDefinitionParserTests
{
    private static string Fence(string body) => $"# A page\n\n```form-definition\n{body}\n```\n\nSome prose.\n";

    [Fact]
    public void ReadsACollectionAndItsFields()
    {
        var (definitions, errors) = FormDefinitionParser.Parse(Fence(
            """
            collection = incident-report
            field = severity: select(low, medium, high), required
            field = summary: text, required
            field = occurredAt: date
            field = costEstimate: number
            """));

        Assert.Empty(errors);
        var form = Assert.Single(definitions);
        Assert.Equal("incident-report", form.Collection);
        Assert.Equal(4, form.Fields.Count);

        var severity = form.Fields[0];
        Assert.Equal(FormFieldType.Select, severity.Type);
        Assert.True(severity.Required);
        // The author's order, not sorted: this is the picker's order, so it is content.
        Assert.Equal(["low", "medium", "high"], severity.Options);

        Assert.Equal(FormFieldType.Date, form.Fields[2].Type);
        Assert.False(form.Fields[2].Required);
        Assert.Equal(FormFieldType.Number, form.Fields[3].Type);
    }

    [Fact]
    public void ReadsSeveralFormsOnOnePage()
    {
        // A page holds any number of collections — an incident page might carry the
        // report, its actions and its sign-off side by side.
        var markdown = Fence("collection = incident-report\nfield = summary: text")
            + Fence("collection = action-item\nfield = owner: text");

        var (definitions, errors) = FormDefinitionParser.Parse(markdown);

        Assert.Empty(errors);
        Assert.Equal(["incident-report", "action-item"], definitions.Select(d => d.Collection));
    }

    [Fact]
    public void RefusesBothHalvesOfADuplicatedCollection()
    {
        // Not last-one-wins. Two definitions competing to describe one record set is how
        // a form quietly changes shape, and neither is more correct than the other — so
        // the page gets neither, and an error naming the collection.
        var markdown = Fence("collection = incident-report\nfield = summary: text")
            + Fence("collection = Incident-Report\nfield = other: text");

        var (definitions, errors) = FormDefinitionParser.Parse(markdown);

        Assert.Empty(definitions);
        Assert.Contains("defined more than once", Assert.Single(errors).Message);
    }

    [Fact]
    public void IgnoresOtherFences()
    {
        var markdown = "```page-list\nquery = label = \"x\"\n```\n" + Fence("collection = notes\nfield = body: text");

        var (definitions, _) = FormDefinitionParser.Parse(markdown);

        Assert.Equal("notes", Assert.Single(definitions).Collection);
    }

    [Theory]
    [InlineData("field = summary: text", "no 'collection = ...' line")]
    [InlineData("collection = notes", "declares no fields")]
    [InlineData("collection = notes\nfield = severity: colour", "not a field type")]
    [InlineData("collection = notes\nfield = severity: select()", "lists no options")]
    [InlineData("collection = notes\nfield = summary", "Could not read the field")]
    [InlineData("collection = notes\nfield = a: text\nfield = a: number", "declared twice")]
    public void ReportsWhatIsWrongRatherThanDroppingTheForm(string body, string expected)
    {
        // A malformed definition is an authoring mistake, and silently discarding it
        // presents to the author as "my form disappeared".
        var (definitions, errors) = FormDefinitionParser.Parse(Fence(body));

        Assert.Empty(definitions);
        Assert.Contains(expected, Assert.Single(errors).Message);
    }

    [Fact]
    public void IgnoresUnknownKeysSoANewerFenceDegradesRatherThanBreaks()
    {
        var (definitions, errors) = FormDefinitionParser.Parse(Fence(
            """
            collection = notes
            # a comment
            somethingFromTheFuture = 42
            field = body: text
            """));

        Assert.Empty(errors);
        Assert.Equal("body", Assert.Single(Assert.Single(definitions).Fields).Name);
    }

    [Fact]
    public void FindsNothingInAPageWithNoForms()
    {
        var (definitions, errors) = FormDefinitionParser.Parse("# Just a page\n\nWith prose.\n");
        Assert.Empty(definitions);
        Assert.Empty(errors);
    }
}
