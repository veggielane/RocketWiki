using RocketWiki.Core.Content;
using Xunit;

namespace RocketWiki.Core.Tests.Content;

/// <summary>The :name: grammar, pinned — this exact behavior is what the phase-2
/// SPA's renderer/autocomplete must port (EmojiName.Pattern is the contract).</summary>
public class EmojiNameTests
{
    [Theory]
    [InlineData("banana")]
    [InlineData("thumbs_up")]
    [InlineData("rocket-9")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("_")]
    [InlineData("-")]
    public void ValidNames_Pass(string name) => Assert.True(EmojiName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Banana")] // uppercase - lowercase-only is what makes uniqueness case-insensitive by construction
    [InlineData("ba nana")]
    [InlineData("ban:ana")] // the delimiter itself
    [InlineData("naïve")] // non-ASCII
    [InlineData("emoji!")]
    [InlineData("tab\tname")]
    public void InvalidNames_Fail(string? name) => Assert.False(EmojiName.IsValid(name));

    [Fact]
    public void LengthCap_IsInclusiveAt64()
    {
        Assert.True(EmojiName.IsValid(new string('a', 64)));
        Assert.False(EmojiName.IsValid(new string('a', 65)));
    }
}
