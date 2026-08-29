using RocketWiki.Core.Forms;
using Xunit;

namespace RocketWiki.Core.Tests.Forms;

/// <summary>
/// The contract between the SPA's insert dialog and this parser.
///
/// <para>The dialog builds a fence body (web/src/forms/fenceBody.ts) and the server
/// parses it. Those are different languages in different repositories' worth of code,
/// and nothing but this test says they agree — a dialog that emitted
/// <c>severity: Select(low)</c> or <c>field: severity = select</c> would look right in
/// its own unit tests and produce a form that silently fails to parse on the page.</para>
///
/// <para>The bodies below are the exact strings
/// <c>buildFormDefinitionFenceBody</c> produces, kept in sync by hand. That is the
/// weak link and it is deliberate: the alternative is running Node from a C# test,
/// which buys less than it costs. If the builder changes shape, this test is where
/// it should be updated.</para>
/// </summary>
public class FormDefinitionFenceContractTests
{
    private static string Fence(string body) => $"```form-definition\n{body}\n```\n";

    [Fact]
    public void TheDialogsSimplestOutput_Parses()
    {
        var body = "collection = notes\nfield = body: text";

        var (definitions, errors) = FormDefinitionParser.Parse(Fence(body));

        Assert.Empty(errors);
        var form = Assert.Single(definitions);
        Assert.Equal("notes", form.Collection);
        Assert.Equal(FormFieldType.Text, Assert.Single(form.Fields).Type);
    }

    [Fact]
    public void TheDialogsFullOutput_Parses_WithEveryTypeAndModifier()
    {
        var body = string.Join('\n',
            "collection = incident-report",
            "field = severity: select(low, medium, high), required",
            "field = summary: text, required",
            "field = occurredAt: date",
            "field = costEstimate: number");

        var (definitions, errors) = FormDefinitionParser.Parse(Fence(body));

        Assert.Empty(errors);
        var form = Assert.Single(definitions);
        Assert.Equal(
            [FormFieldType.Select, FormFieldType.Text, FormFieldType.Date, FormFieldType.Number],
            form.Fields.Select(f => f.Type));
        Assert.Equal(["low", "medium", "high"], form.Fields[0].Options);
        Assert.True(form.Fields[0].Required);
        Assert.False(form.Fields[2].Required);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("NUMBER")]
    [InlineData("DATE")]
    public void EveryTypeTokenTheDialogCanWrite_IsOneTheParserKnows(string graphQlEnumName)
    {
        // The dialog lower-cases the GraphQL enum name to make its token. This pins
        // that the transformation lands on a real type for every member rather than
        // for the three someone happened to try.
        var body = $"collection = notes\nfield = a: {graphQlEnumName.ToLowerInvariant()}";

        var (definitions, errors) = FormDefinitionParser.Parse(Fence(body));

        Assert.Empty(errors);
        Assert.Single(Assert.Single(definitions).Fields);
    }
}
