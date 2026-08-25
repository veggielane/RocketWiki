using RocketWiki.Core.Access;
using Xunit;

namespace RocketWiki.Core.Tests.Access;

/// <summary>
/// design.md §21: the eyes-only country vocabulary is the registered <c>nationality</c>
/// attribute's allowed values, parsed here. Every failure mode has to end at "no
/// vocabulary", because "no vocabulary" is what makes the service refuse to create a
/// caveat that could never match anyone.
/// </summary>
public class NationalityVocabularyTests
{
    [Fact]
    public void Parse_CanonicalizesEveryDeclaredValue()
    {
        var values = NationalityVocabulary.Parse("""["gb", " us ", "NZ"]""");

        Assert.Equal(["GB", "NZ", "US"], values.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoDeclaredValues_IsEmpty(string? json) => Assert.Empty(NationalityVocabulary.Parse(json));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"nope\": true}")]  // an object, not an array
    [InlineData("\"GB\"")]            // a bare string, not an array
    [InlineData("[1, 2, 3]")]         // numbers are not country values
    [InlineData("[\"\", \"  \"]")]    // blanks contribute nothing
    public void Parse_MalformedOrUnusable_IsEmpty_NeverAThrow(string json) =>
        // Fail closed, exactly as a malformed rule expression denies (§6.3): an
        // unparseable vocabulary means no country validates, which means no eyes-only
        // caveat can be created - the safe end of the failure.
        Assert.Empty(NationalityVocabulary.Parse(json));

    [Fact]
    public void Parse_MixedArray_KeepsOnlyTheUsableStrings() =>
        Assert.Equal(["GB"], NationalityVocabulary.Parse("""["gb", 7, null, true]"""));
}
