using System.Globalization;
using RocketWiki.Data.Services;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Storage keys are generated once and stored on the row, so a wrong prefix never fails
/// loudly — old objects stay readable and only the meaning of the date segment quietly
/// changes. That segment exists so a bucket listing is navigable and a retention sweep
/// can work by prefix; a janitor scanning "attachments/2026/08" would skip whatever a
/// differently-configured server had written under "attachments/1447/02".
/// </summary>
public class StorageKeysTests
{
    [Theory]
    [InlineData("ar-SA")]  // Umm al-Qura calendar
    [InlineData("th-TH")]  // Buddhist calendar: 2026 -> 2569
    [InlineData("fa-IR")]  // Persian calendar
    [InlineData("en-GB")]
    public void KeyPrefixesAreGregorian_WhateverTheCurrentCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var instant = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

            Assert.StartsWith("attachments/2026/08/", StorageKeys.ForAttachment(instant), StringComparison.Ordinal);
            Assert.StartsWith("avatars/2026/08/", StorageKeys.ForAvatar(instant), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void EachKeyIsUnique()
    {
        // Non-vacuity for the assertions above: the prefix is fixed, the rest must not be.
        var instant = DateTime.UtcNow;
        Assert.NotEqual(StorageKeys.ForAttachment(instant), StorageKeys.ForAttachment(instant));
    }
}
