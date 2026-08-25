using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;

namespace RocketWiki.Data.Tests;

/// <summary>Minimal, valid entity graphs shared across tests, so each test only spells out what it's actually checking.</summary>
internal static class TestData
{
    public static User NewUser(string displayName = "Test User") => new()
    {
        DisplayName = displayName,
        Subject = $"sub-{Guid.NewGuid():N}",
        AttributesJson = "{}",
        CreatedAtUtc = DateTime.UtcNow,
        LastSeenAtUtc = DateTime.UtcNow,
    };

    public static Space NewSpace(string key = "ENG") => new()
    {
        Key = key,
        Name = $"{key} Space",
        OriginInstanceId = "local-instance",
        CreatedAtUtc = DateTime.UtcNow,
        CreatedByUserId = Guid.NewGuid(),
    };

    public static Page NewPage(Space space, string slug = "home", Page? parent = null) => new()
    {
        SpaceId = space.Id,
        ParentPageId = parent?.Id,
        AncestorPath = parent is null ? "/" : $"{parent.AncestorPath}{parent.Id}/",
        Slug = slug,
        Title = slug,
        CurrentContent = $"# {slug}",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// A protective marking for a page (design.md §21). Tests that do NOT call this still
    /// get an OFFICIAL marking: RocketWikiDbContext materializes one for any page inserted
    /// without one, which is the every-page-is-marked invariant enforced at the
    /// persistence seam. Call this only when the marking is the thing under test.
    /// </summary>
    public static PageMarking NewMarking(Page page, ClassificationLevel level, params string[] eyesOnly) =>
        NewMarkingWithPrefix(page, level, ProtectiveMarking.DefaultPrefix, eyesOnly);

    /// <summary>
    /// As above, with an explicit national prefix (design.md §21.12) — for the tests
    /// where the prefix itself is what is under test. Null means no prefix.
    ///
    /// <para>Deliberately a DIFFERENT NAME rather than an overload: an overload taking
    /// <c>(…, string? prefix, params string[] eyesOnly)</c> would silently steal every
    /// existing <c>NewMarking(page, level, "GB")</c> call, because C# prefers the
    /// non-expanded form and a lone string matches <c>prefix</c> exactly. Every
    /// single-country marking in the suite would have quietly become a prefix with no
    /// caveat — which is exactly the kind of silent test weakening that makes a green
    /// run meaningless.</para>
    /// </summary>
    public static PageMarking NewMarkingWithPrefix(
        Page page, ClassificationLevel level, string? prefix, params string[] eyesOnly)
    {
        var marking = new PageMarking
        {
            PageId = page.Id,
            Level = level,
            Prefix = ProtectiveMarking.CanonicalizePrefix(prefix),
            SetAtUtc = DateTime.UtcNow,
        };
        foreach (var country in ProtectiveMarking.Create(level, eyesOnly).EyesOnly)
        {
            marking.Countries.Add(new PageMarkingCountry { PageId = page.Id, CountryValue = country });
        }

        return marking;
    }

    public static PageRevision NewRevision(Page page, User author, int revisionNumber = 1) => new()
    {
        PageId = page.Id,
        RevisionNumber = revisionNumber,
        Title = page.Title,
        Content = page.CurrentContent,
        AuthorUserId = author.Id,
        CreatedAtUtc = DateTime.UtcNow,
    };
}
