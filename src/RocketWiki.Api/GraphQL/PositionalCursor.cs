using System.Globalization;
using System.Text;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Opaque cursors that are positions into an already-materialized, permission-filtered
/// sequence — the shape both hand-rolled connections in this schema use.
///
/// <para>The prefix namespaces one connection's cursors from another's, so a cursor lifted
/// from one field and pasted into another is rejected rather than silently interpreted as a
/// position in a different list.</para>
///
/// <para><c>Query.Search</c> has an equivalent inline pair that predates this helper and is
/// deliberately left alone: extracting it would be a behaviour-preserving edit to a shipped
/// field for no benefit to either caller, and the two are pinned by their own tests.</para>
/// </summary>
internal static class PositionalCursor
{
    public static string Encode(string prefix, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(prefix + index.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The index to start from: the position after <paramref name="after"/>, or 0. Cursors
    /// are opaque to clients, so a malformed or foreign one means "from the top" rather than
    /// an error — defensive, and what a stale bookmark deserves.
    /// </summary>
    public static int DecodeAfter(string prefix, string? after)
    {
        if (string.IsNullOrEmpty(after))
        {
            return 0;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(after));
            if (decoded.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(
                    decoded[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0)
            {
                return index + 1;
            }
        }
        catch (FormatException)
        {
            // Not base64 - fall through to "from the top".
        }

        return 0;
    }
}
