using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Data.Tests;

/// <summary>
/// Proves the model actually persists and reloads through a real ADO.NET provider -
/// hand-reading the migration can't catch a value converter or type mapping that's wrong
/// in a way that only shows up round-tripping through the database.
/// </summary>
public class EntityRoundTripTests : SqliteTestBase
{
    [Fact]
    public void SpacePageRevision_RoundTrip_PreservesScalarValues()
    {
        var user = TestData.NewUser("Alice Author");
        var space = TestData.NewSpace("ENG");
        var page = TestData.NewPage(space, "getting-started");
        var revision = TestData.NewRevision(page, user);
        page.CurrentRevisionNumber = revision.RevisionNumber;

        using (var writeContext = CreateContext())
        {
            writeContext.Users.Add(user);
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.PageRevisions.Add(revision);
            writeContext.SaveChanges();
        }

        // Reload via a fresh context so this reads from SQLite, not the change tracker.
        using var readContext = CreateContext();
        var reloadedPage = readContext.Pages.Single(p => p.Id == page.Id);
        var reloadedRevision = readContext.PageRevisions.Single(r => r.Id == revision.Id);

        Assert.Equal(space.Id, reloadedPage.SpaceId);
        Assert.Equal("getting-started", reloadedPage.Slug);
        Assert.Equal("# getting-started", reloadedPage.CurrentContent);
        Assert.Equal(1, reloadedRevision.RevisionNumber);
        Assert.Equal(user.Id, reloadedRevision.AuthorUserId);
    }

    [Fact]
    public void Attachment_ContentHash_RoundTripsExactBytes()
    {
        var user = TestData.NewUser();
        var space = TestData.NewSpace();
        var page = TestData.NewPage(space);
        var hash = new byte[32];
        Random.Shared.NextBytes(hash);

        var attachment = new Attachment
        {
            PageId = page.Id,
            FileName = "diagram.png",
            ContentType = "image/png",
            SizeBytes = 12345,
            ContentHash = hash,
            StorageKey = "attachments/2026/08/abc123",
            UploadedByUserId = user.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Users.Add(user);
            writeContext.Spaces.Add(space);
            writeContext.Pages.Add(page);
            writeContext.Attachments.Add(attachment);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloaded = readContext.Attachments.Single(a => a.Id == attachment.Id);

        Assert.Equal(hash, reloaded.ContentHash);
    }

    [Fact]
    public void AccessRule_ExpressionJson_RoundTrips()
    {
        var space = TestData.NewSpace();
        const string json = """{ "allOf": [ { "group": "engineering" }, { "everyone": true } ] }""";
        var rule = new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = json,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = Guid.NewGuid(),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = Guid.NewGuid(),
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.AccessRules.Add(rule);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloaded = readContext.AccessRules.Single(r => r.Id == rule.Id);

        Assert.Equal(json, reloaded.ExpressionJson);
        Assert.Equal(AccessRuleKind.SpaceGrant, reloaded.Kind);
        Assert.Equal(SpaceRole.Viewer, reloaded.Role);
        Assert.Null(reloaded.Action);
        Assert.Null(reloaded.PageId);
    }

    [Fact]
    public void Label_PageLabel_ManyToMany_RoundTrips()
    {
        var space = TestData.NewSpace();
        var pageA = TestData.NewPage(space, "page-a");
        var pageB = TestData.NewPage(space, "page-b");
        var label = new Label { SpaceId = space.Id, Name = "how-to" };

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.Pages.AddRange(pageA, pageB);
            writeContext.Labels.Add(label);
            writeContext.PageLabels.AddRange(
                new PageLabel { PageId = pageA.Id, LabelId = label.Id },
                new PageLabel { PageId = pageB.Id, LabelId = label.Id });
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var linkedPageIds = readContext.PageLabels
            .Where(pl => pl.LabelId == label.Id)
            .Select(pl => pl.PageId)
            .ToList();

        Assert.Equal(2, linkedPageIds.Count);
        Assert.Contains(pageA.Id, linkedPageIds);
        Assert.Contains(pageB.Id, linkedPageIds);
    }

    [Fact]
    public void LookupTables_AttributeDefinitionAndKnownGroup_RoundTrip()
    {
        var attribute = new AttributeDefinition
        {
            Key = "nationality",
            ClaimName = "nationality",
            DisplayName = "Nationality",
            Type = AttributeValueType.StringArray,
            AllowedValuesJson = """["NZ", "US", "GB"]""",
        };
        var group = new KnownGroup
        {
            Name = "engineering",
            Source = KnownGroupSource.ObservedAtLogin,
            FirstSeenAtUtc = DateTime.UtcNow,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.AttributeDefinitions.Add(attribute);
            writeContext.KnownGroups.Add(group);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloadedAttribute = readContext.AttributeDefinitions.Single(a => a.Key == "nationality");
        var reloadedGroup = readContext.KnownGroups.Single(g => g.Name == "engineering");

        Assert.Equal(AttributeValueType.StringArray, reloadedAttribute.Type);
        Assert.Equal(KnownGroupSource.ObservedAtLogin, reloadedGroup.Source);
    }

    [Fact]
    public void SyncState_StringPrimaryKeys_RoundTrip()
    {
        var space = TestData.NewSpace();
        var importState = new SyncImportState
        {
            OriginInstanceId = "low-instance-1",
            LastBundleNumber = 41,
            LastManifestHash = new string('a', 64),
            LastImportAtUtc = DateTime.UtcNow,
        };
        var spaceState = new SyncSpaceState
        {
            OriginInstanceId = "low-instance-1",
            SpaceId = space.Id,
            AppliedSequence = 1337,
        };

        using (var writeContext = CreateContext())
        {
            writeContext.Spaces.Add(space);
            writeContext.SyncImportStates.Add(importState);
            writeContext.SyncSpaceStates.Add(spaceState);
            writeContext.SaveChanges();
        }

        using var readContext = CreateContext();
        var reloadedImport = readContext.SyncImportStates.Single(s => s.OriginInstanceId == "low-instance-1");
        var reloadedSpaceState = readContext.SyncSpaceStates
            .Single(s => s.OriginInstanceId == "low-instance-1" && s.SpaceId == space.Id);

        Assert.Equal(41, reloadedImport.LastBundleNumber);
        Assert.Equal(1337, reloadedSpaceState.AppliedSequence);
    }
}
