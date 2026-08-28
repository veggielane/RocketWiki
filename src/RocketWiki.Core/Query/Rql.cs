using System.Globalization;

namespace RocketWiki.Core.Query;

/// <summary>
/// The outcome of parsing one RQL string: either a validated query or a list of positioned
/// errors, never both and never neither.
/// </summary>
public sealed class RqlParseResult
{
    private RqlParseResult(RqlQuery? query, IReadOnlyList<RqlError> errors)
    {
        Query = query;
        Errors = errors;
        Canonical = query is null ? null : RqlPrinter.Print(query);
    }

    /// <summary>The validated query, or null when <see cref="Errors"/> is non-empty.</summary>
    public RqlQuery? Query { get; }

    public IReadOnlyList<RqlError> Errors { get; }

    /// <summary>The canonical printed form of <see cref="Query"/>, or null when the parse failed.</summary>
    public string? Canonical { get; }

    public bool IsValid => Query is not null;

    internal static RqlParseResult Success(RqlQuery query) => new(query, []);

    internal static RqlParseResult Failure(IReadOnlyList<RqlError> errors) =>
        new(null, errors.Count > 0 ? errors : [new RqlError(RqlErrorCode.Syntax, "Invalid query.", RqlSpan.Empty)]);
}

/// <summary>
/// RQL — RocketWiki's query language (design.md §22). The entry point: a string in, a
/// validated AST or positioned errors out. Pure: no I/O, no clock, no database, no
/// principal.
///
/// <para><b>The string is the canonical form.</b> There is deliberately no structured AST
/// <i>input</i> anywhere in the system — a client-built tree would need exactly this
/// validation applied to it anyway, and two input paths into the compiler is twice the
/// surface to keep the closed field set closed on. The AST is published as <i>output</i>
/// only (the <c>parseRql</c> GraphQL field), which is all a builder UI needs.</para>
///
/// <para>Not named CQL: that is Atlassian's name for Confluence's query language, and
/// borrowing it would imply a compatibility promise this grammar does not keep.</para>
/// </summary>
public static class Rql
{
    public static RqlParseResult Parse(string? query)
    {
        var text = query ?? string.Empty;

        if (text.Length > RqlLimits.MaxQueryLength)
        {
            return RqlParseResult.Failure([
                new RqlError(
                    RqlErrorCode.TooComplex,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Query is too long: {0} characters, limit {1}.",
                        text.Length,
                        RqlLimits.MaxQueryLength),
                    new RqlSpan(0, text.Length)),
            ]);
        }

        var (tokens, lexErrors) = RqlLexer.Tokenize(text);
        if (lexErrors.Count > 0)
        {
            return RqlParseResult.Failure(lexErrors);
        }

        var (raw, parseErrors) = RqlParser.Parse(tokens);
        if (raw is null)
        {
            return RqlParseResult.Failure(parseErrors);
        }

        var (validated, validationErrors) = RqlValidator.Validate(raw);
        return validated is null ? RqlParseResult.Failure(validationErrors) : RqlParseResult.Success(validated);
    }

    /// <summary>The canonical string for a query — see <see cref="RqlPrinter"/> for why it is a fixed point.</summary>
    public static string Print(RqlQuery query) => RqlPrinter.Print(query);
}
