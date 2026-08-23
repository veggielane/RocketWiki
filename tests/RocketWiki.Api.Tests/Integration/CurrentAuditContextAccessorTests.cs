using Microsoft.AspNetCore.Http;
using RocketWiki.Api.Audit;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Direct unit coverage of the pure path-to-channel mapping (design.md §7) —
/// faster and more precise than asserting on a live HTTP round trip per path,
/// and exactly what <c>InternalsVisibleTo</c> on RocketWiki.Api exists for.
/// </summary>
public sealed class CurrentAuditContextAccessorTests
{
    [Theory]
    [InlineData("/graphql", AuditChannel.GraphQl)]
    [InlineData("/graphql/", AuditChannel.GraphQl)]
    [InlineData("/mcp", AuditChannel.Mcp)]
    [InlineData("/attachments/123", AuditChannel.Attachment)]
    // The emoji binary routes share the Attachment channel deliberately - it names
    // the HTTP surface, not the subject (see DetermineChannel's own comment).
    [InlineData("/emojis/banana", AuditChannel.Attachment)]
    public void KnownPath_MapsToExpectedChannel(string path, AuditChannel expected)
    {
        var actual = CurrentAuditContextAccessor.DetermineChannel(new PathString(path));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/")]
    [InlineData("/something-unrelated")]
    public void UnknownPath_MapsToNoChannel(string path)
    {
        var actual = CurrentAuditContextAccessor.DetermineChannel(new PathString(path));

        Assert.Null(actual);
    }
}
