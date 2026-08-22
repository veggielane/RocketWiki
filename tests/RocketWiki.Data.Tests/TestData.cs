using RocketWiki.Core.Entities;

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
