using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Content;

/// <summary>
/// The wire form of a page icon, pinned — this one converter is what the EF column
/// mapping, the sync bundle and the GraphQL enum all spell, so "the database, the
/// bundle and the API read identically" is a property of this function and nothing
/// else. The degradation rule (an unrecognised name reads back as null rather than
/// throwing) is a cross-component contract: BundleImportService leans on it so a
/// bundle from a newer instance costs a page its decoration instead of stranding
/// every other change behind it, and the SPA's picker leans on it to keep an
/// unrecognised icon selected.
/// </summary>
public class PageIconTests
{
    public static TheoryData<PageIcon> AllIcons()
    {
        var data = new TheoryData<PageIcon>();
        foreach (var icon in Enum.GetValues<PageIcon>())
        {
            data.Add(icon);
        }

        return data;
    }

    [Theory]
    [InlineData(PageIcon.Document, "DOCUMENT")]
    [InlineData(PageIcon.Checklist, "CHECKLIST")]
    [InlineData(PageIcon.Lightbulb, "LIGHTBULB")]
    [InlineData(PageIcon.Rocket, "ROCKET")]
    public void ToWireName_IsTheUpperSnakeMemberName(PageIcon icon, string expected) =>
        Assert.Equal(expected, PageIcons.ToWireName(icon));

    [Theory]
    [MemberData(nameof(AllIcons))]
    public void EveryMember_RoundTripsThroughItsWireName(PageIcon icon) =>
        Assert.Equal(icon, PageIcons.FromWireName(PageIcons.ToWireName(icon)));

    /// <summary>
    /// Not decoration on the round-trip test above: two members sharing a wire name
    /// would make FromWireName return whichever Enum.GetValues happened to reach first,
    /// and the round trip would still pass for one of them.
    /// </summary>
    [Fact]
    public void WireNames_AreUniqueAcrossMembers()
    {
        var wireNames = Enum.GetValues<PageIcon>().Select(PageIcons.ToWireName).ToList();
        Assert.Equal(wireNames.Count, wireNames.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The tripwire for the EF column bound. A member whose wire name outgrew
    /// MaxWireNameLength would be truncated by SQL Server and read back as an
    /// unrecognised name — i.e. it would silently lose its icon in production, and
    /// nowhere else would say so: SQLite does not enforce declared lengths at all
    /// (see SqliteTestBase's doc), so the Data tier's round-trip cannot catch it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllIcons))]
    public void EveryWireName_FitsTheColumnBound(PageIcon icon) =>
        Assert.True(
            PageIcons.ToWireName(icon).Length <= PageIcons.MaxWireNameLength,
            $"{icon} has a wire name longer than PageIcons.MaxWireNameLength ({PageIcons.MaxWireNameLength}).");

    [Theory]
    [InlineData("NOT_AN_ICON")]
    [InlineData("Document ")] // trailing space - not trimmed, so not a match
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromWireName_UnrecognisedOrBlank_ReturnsNull(string? wireName) =>
        Assert.Null(PageIcons.FromWireName(wireName));

    /// <summary>
    /// Case-insensitive on the way in, so a hand-edited database row or a bundle from
    /// a tool that lower-cased its JSON still resolves rather than silently dropping
    /// the icon. ToWireName remains the single canonical spelling on the way out.
    /// </summary>
    [Theory]
    [InlineData("document")]
    [InlineData("Document")]
    [InlineData("DOCUMENT")]
    public void FromWireName_IsCaseInsensitive(string wireName) =>
        Assert.Equal(PageIcon.Document, PageIcons.FromWireName(wireName));
}
