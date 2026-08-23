using RocketWiki.Core.Content;
using Xunit;

namespace RocketWiki.Core.Tests.Content;

/// <summary>
/// design.md §4/§8: mentions are serialized as <c>@[display](user://{id})</c> — the
/// exact form web/src/editor/markdown/mentionRule.ts emits (its MENTION_RE is the
/// mirror of the pattern under test). The corpus below includes that frontend test
/// suite's own shapes (web/src/editor/__tests__/roundtrip.test.ts) so the two parsers
/// can't quietly diverge on what counts as a mention.
/// </summary>
public class MentionParserTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void SingleMention_IsExtracted()
    {
        var ids = MentionParser.ExtractMentionedUserIds($"Thanks @[Ada Lovelace](user://{UserA}) for the review.");
        Assert.Equal(new[] { UserA }, ids);
    }

    [Fact]
    public void MultipleDistinctMentions_ComeBackInFirstAppearanceOrder()
    {
        var ids = MentionParser.ExtractMentionedUserIds(
            $"Ping @[B](user://{UserB}) and @[A](user://{UserA}) and @[B again](user://{UserB}).");
        Assert.Equal(new[] { UserB, UserA }, ids);
    }

    [Fact]
    public void DuplicateMentionsOfSameUser_AreDeduplicated()
    {
        var ids = MentionParser.ExtractMentionedUserIds($"@[A](user://{UserA}) @[A](user://{UserA})");
        Assert.Equal(new[] { UserA }, ids);
    }

    [Fact]
    public void NonGuidUserId_IsIgnored()
    {
        // The frontend round-trip corpus uses ids like "user-42" - syntactically a
        // mention, but it cannot name a local User row, so no one is notified.
        var ids = MentionParser.ExtractMentionedUserIds("Thanks @[Ada Lovelace](user://user-42) for the review.");
        Assert.Empty(ids);
    }

    [Fact]
    public void OrdinaryLinksAndPageLinks_AreNotMentions()
    {
        var ids = MentionParser.ExtractMentionedUserIds(
            "See [the docs](https://example.com) and [Setup](page://page-1).");
        Assert.Empty(ids);
    }

    [Fact]
    public void BareAtSign_AndPlainEmail_AreNotMentions()
    {
        var ids = MentionParser.ExtractMentionedUserIds($"mail me @ home or ada@example.com {UserA}");
        Assert.Empty(ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no mentions here")]
    public void EmptyOrMentionFreeContent_YieldsNothing(string? markdown)
    {
        Assert.Empty(MentionParser.ExtractMentionedUserIds(markdown));
    }

    [Fact]
    public void MentionInsideSurroundingProse_MatchesMentionRuleTsShape()
    {
        // Mirror of the mixed-inline case in web/src/editor/__tests__/roundtrip.test.ts.
        var ids = MentionParser.ExtractMentionedUserIds(
            $"Ping @[Ada Lovelace](user://{UserA}) re [the docs](https://example.com) and [Setup](page://page-1).\n");
        Assert.Equal(new[] { UserA }, ids);
    }

    [Fact]
    public void EmptyDisplayText_StillParses()
    {
        // mentionRule.ts's display group is [^\]]* - empty display is legal there too.
        var ids = MentionParser.ExtractMentionedUserIds($"@[](user://{UserA})");
        Assert.Equal(new[] { UserA }, ids);
    }

    [Fact]
    public void WhitespaceInsideUrl_DoesNotParse()
    {
        // mentionRule.ts's id group is [^)\s]+ - whitespace ends the candidate.
        var ids = MentionParser.ExtractMentionedUserIds($"@[A](user://{UserA} )");
        Assert.Empty(ids);
    }
}
