using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// The shared, provider-independent key validation both providers run before any
/// I/O (design.md §10: keys are opaque and system-generated — this layer exists so
/// a future bug routing user-influenced text into a key still can't become path
/// traversal or a mis-named object; see StorageKey's own doc for the per-rule
/// reasoning). Provider-specific checks on top of this — rooted Windows paths,
/// resolved-path containment — are covered by FileSystemFileStorageTests.
/// </summary>
public sealed class StorageKeyTests
{
    [Theory]
    [InlineData("attachments/2026/08/2b1f0f5e2c4d4d3d9c1a7b6f5e4d3c2b")]
    [InlineData("avatars/512/some-user")]
    [InlineData("emojis/rocket.png")]
    [InlineData("single-segment")]
    [InlineData("trailing/slash/")]
    [InlineData("dot.in/segment..name")]
    public void Validate_AcceptsWellFormedKeys(string key)
    {
        StorageKey.Validate(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Validate_RejectsNullEmptyOrWhitespace(string? key)
    {
        Assert.Throws<ArgumentException>(() => StorageKey.Validate(key!));
    }

    [Theory]
    [InlineData("attachments\\2026\\08\\file.bin")]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\Windows\\System32\\config")]
    public void Validate_RejectsBackslashes(string key)
    {
        Assert.Throws<ArgumentException>(() => StorageKey.Validate(key));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/attachments/2026/08/file.bin")]
    public void Validate_RejectsLeadingSlash(string key)
    {
        Assert.Throws<ArgumentException>(() => StorageKey.Validate(key));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("attachments/../../outside.txt")]
    [InlineData("attachments/2026/../../../outside.txt")]
    [InlineData("attachments/..")]
    [InlineData("./attachments/file.bin")]
    [InlineData("attachments/./file.bin")]
    public void Validate_RejectsDotAndDotDotSegments(string key)
    {
        Assert.Throws<ArgumentException>(() => StorageKey.Validate(key));
    }
}
