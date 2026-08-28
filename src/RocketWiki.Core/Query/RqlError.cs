namespace RocketWiki.Core.Query;

/// <summary>
/// One thing wrong with a query, positioned so an editor can underline it (design.md §22).
///
/// <para><b>Errors are about syntax and vocabulary, never about existence.</b> Nothing in
/// this type may ever report that a space, label, or user is unknown, restricted, or
/// invisible: design.md §6.7 requires an invisible space to be indistinguishable from a
/// nonexistent one, and a "helpful" error naming the difference is exactly the leak. The
/// validator therefore never touches the database, and <c>parseRql</c> is safe to answer
/// for any authenticated caller precisely because of that.</para>
/// </summary>
/// <param name="Offset">Start of the offending text, in UTF-16 code units from the start of the query.</param>
/// <param name="Length">Length of the offending text; may be 0 at end-of-input.</param>
public sealed record RqlError(RqlErrorCode Code, string Message, int Offset, int Length)
{
    public RqlError(RqlErrorCode code, string message, RqlSpan span)
        : this(code, message, span.Offset, span.Length)
    {
    }
}
