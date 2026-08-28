using RocketWiki.Core.Query;
using Xunit;

namespace RocketWiki.Core.Tests.Query;

/// <summary>
/// The closed field set and the rest of RQL's vocabulary rules (design.md §22).
///
/// <para>The load-bearing tests here are the first two. Classification and permission state
/// must be refused <b>by name, with a distinct message</b> — not silently ignored (the author
/// would believe the filter applied) and not reported as unknown fields (which invites a
/// retry with a different spelling) — and <c>text</c> must say it is not supported yet rather
/// than pretend it was never a field.</para>
/// </summary>
public class RqlValidatorTests
{
    private static RqlError SingleError(string text)
    {
        var result = Rql.Parse(text);
        Assert.False(result.IsValid, "Expected a validation failure for: " + text);
        return Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("marking")]
    [InlineData("markings")]
    [InlineData("classification")]
    [InlineData("level")]
    [InlineData("eyesOnly")]
    [InlineData("eyes_only")]
    [InlineData("caveat")]
    [InlineData("prefix")]
    [InlineData("restricted")]
    [InlineData("restriction")]
    [InlineData("restrictions")]
    [InlineData("permission")]
    [InlineData("permissions")]
    [InlineData("clearance")]
    [InlineData("group")]
    [InlineData("nationality")]
    [InlineData("MARKING")]
    [InlineData("Clearance")]
    public void ClassificationAndPermissionFields_AreRefusedByNameAsNotQueryable(string field)
    {
        var error = SingleError($"""{field} = "x" """);

        Assert.Equal(RqlErrorCode.NotQueryableField, error.Code);
        Assert.Contains($"Field '{field}' is not queryable", error.Message, StringComparison.Ordinal);
        Assert.Contains("design.md §21.8", error.Message, StringComparison.Ordinal);

        // Distinct from the unknown-field message, and positioned on the field name.
        Assert.DoesNotContain("Unknown field", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, error.Offset);
        Assert.Equal(field.Length, error.Length);
    }

    [Fact]
    public void NotQueryableFields_AreAlsoRefusedInOrderBy()
    {
        var error = SingleError("""label = "a" ORDER BY classification""");

        Assert.Equal(RqlErrorCode.NotQueryableField, error.Code);
        Assert.Contains("not queryable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_IsRefusedAsNotYetSupported_NotAsUnknown()
    {
        var error = SingleError("""text ~ "turbopump" """);

        Assert.Equal(RqlErrorCode.UnsupportedField, error.Code);
        Assert.Contains("Field 'text' is not supported yet", error.Message, StringComparison.Ordinal);
        Assert.Contains("`search` field", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown field", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not queryable", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("body")]
    [InlineData("labels")]
    [InlineData("spaceKey")]
    [InlineData("author")]
    public void AnythingElse_IsAnUnknownFieldNamingTheAllowedSet(string field)
    {
        var error = SingleError($"""{field} = "x" """);

        Assert.Equal(RqlErrorCode.UnknownField, error.Code);
        Assert.Equal($"Unknown field '{field}'. Queryable fields are: created, creator, label, space, title, updated.", error.Message);
    }

    [Theory]
    [InlineData("""label ~ "a" """, RqlField.Label)]
    [InlineData("""label > "a" """, RqlField.Label)]
    [InlineData("""space ~ "a" """, RqlField.Space)]
    [InlineData("space IS EMPTY", RqlField.Space)]
    [InlineData("""title > "a" """, RqlField.Title)]
    [InlineData("""title IN ("a")""", RqlField.Title)]
    [InlineData("""created ~ "2026-01-31" """, RqlField.Created)]
    [InlineData("""updated IN ("2026-01-31")""", RqlField.Updated)]
    [InlineData("""creator ~ "a" """, RqlField.Creator)]
    [InlineData("""creator IN ("a")""", RqlField.Creator)]
    [InlineData("creator IS EMPTY", RqlField.Creator)]
    public void OperatorsAreCheckedAgainstTheFieldsType(string text, RqlField field)
    {
        var error = SingleError(text);

        Assert.Equal(RqlErrorCode.OperatorNotAllowed, error.Code);
        Assert.Contains($"for field '{RqlVocabulary.NameOf(field)}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderBy_AcceptsOnlySortableFields()
    {
        foreach (var field in new[] { "label", "space", "creator" })
        {
            var error = SingleError($"""label = "a" ORDER BY {field}""");
            Assert.Equal(RqlErrorCode.OperatorNotAllowed, error.Code);
            Assert.Contains("cannot be used in ORDER BY", error.Message, StringComparison.Ordinal);
            Assert.Contains("created, title, updated", error.Message, StringComparison.Ordinal);
        }

        Assert.True(Rql.Parse("""label = "a" ORDER BY created DESC, title ASC, updated DESC""").IsValid);
    }

    [Fact]
    public void CurrentUser_IsOnlyValidForCreator()
    {
        Assert.True(Rql.Parse("creator = currentUser()").IsValid);

        foreach (var text in new[]
                 {
                     "title = currentUser()", "label = currentUser()", "space = currentUser()",
                     "created > currentUser()",
                 })
        {
            var error = SingleError(text);
            Assert.Equal(RqlErrorCode.FunctionNotAllowedHere, error.Code);
            Assert.Contains("currentUser() is only valid for field 'creator'", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Now_IsOnlyValidForDateFields()
    {
        Assert.True(Rql.Parse("""updated >= now("-7d")""").IsValid);
        Assert.True(Rql.Parse("created < now()").IsValid);

        var error = SingleError("creator = now()");
        Assert.Equal(RqlErrorCode.FunctionNotAllowedHere, error.Code);
        Assert.Contains("now() is only valid for the date fields", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""currentUser("x")""", RqlErrorCode.InvalidValue, "takes no arguments")]
    [InlineData("nonsense()", RqlErrorCode.UnknownFunction, "Unknown function 'nonsense()'")]
    public void FunctionArityAndNamesAreChecked(string call, RqlErrorCode code, string fragment)
    {
        var error = SingleError($"creator = {call}");

        Assert.Equal(code, error.Code);
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""now("tomorrow")""")]
    [InlineData("""now("-7y")""")]
    [InlineData("""now("d")""")]
    [InlineData("""now("999999999d")""")]
    public void InvalidNowOffsetsAreRejectedWithTheUnitList(string call)
    {
        var error = SingleError($"updated > {call}");

        Assert.Equal(RqlErrorCode.InvalidValue, error.Code);
        Assert.Contains("is not a valid now() offset", error.Message, StringComparison.Ordinal);
        Assert.Contains("w (weeks), d (days), h (hours) or m (minutes)", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("31/01/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026")]
    public void InvalidDateLiteralsAreRejected(string literal)
    {
        var error = SingleError($"""created > "{literal}" """);

        Assert.Equal(RqlErrorCode.InvalidValue, error.Code);
        Assert.Contains($"'{literal}' is not a valid date for field 'created'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVocabularyMistakeIsReported_NotJustTheFirst()
    {
        // A syntax error stops at the first (past it the token stream means nothing), but a
        // sound tree with several bad names has several real things to fix, and the editor
        // underlines them all at once.
        var result = Rql.Parse("""marking = "SECRET" AND text ~ "x" AND nonsense = "y" """);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
        Assert.Equal(
            [RqlErrorCode.NotQueryableField, RqlErrorCode.UnsupportedField, RqlErrorCode.UnknownField],
            result.Errors.Select(e => e.Code).ToArray());

        // Each positioned on its own field name.
        Assert.Equal(0, result.Errors[0].Offset);
        Assert.Equal(23, result.Errors[1].Offset);
        Assert.Equal(38, result.Errors[2].Offset);
    }

    [Fact]
    public void NoErrorMessageEverMentionsExistence()
    {
        // design.md §6.7: validation is about syntax and vocabulary, never existence. A
        // message that could say "no such space" is the leak, so nothing here may hint at it.
        string[] probes =
        [
            """space = "BLACKPROJECT" """,
            """label = "nonexistent-label" """,
            """creator = "nobody" """,
            "marking = SECRET",
            """text ~ "x" """,
            "bogus = x",
        ];

        foreach (var probe in probes)
        {
            foreach (var error in Rql.Parse(probe).Errors)
            {
                foreach (var forbidden in new[] { "no such", "does not exist", "not found", "cannot see", "no access" })
                {
                    Assert.DoesNotContain(forbidden, error.Message, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // ...and the three probes naming things that may or may not exist parse cleanly:
        // whether they do is decided by executing the query, not by validating it.
        Assert.True(Rql.Parse("""space = "BLACKPROJECT" """).IsValid);
        Assert.True(Rql.Parse("""label = "nonexistent-label" """).IsValid);
        Assert.True(Rql.Parse("""creator = "nobody" """).IsValid);
    }
}
