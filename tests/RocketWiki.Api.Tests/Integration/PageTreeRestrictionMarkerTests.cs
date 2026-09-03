using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Api.GraphQL;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.4/§6.6: the tree's restriction markers - hasRestrictions for the
/// lock badge, ownViewRestrictions (rule id + expression) for the move dialog's
/// visibility-change warning - computed inside the same single tree walk, and
/// leak-tested: an edit rule's contents never appear (only its existence, via the
/// boolean), a protected node's rule EXPRESSION never appears (its id does, by design -
/// §21.8), and every expression that DOES appear is one the caller provably passed.
/// Fixture:
///
/// <code>
/// Space (access: everyone)
/// ├── PageA (unrestricted)
/// │   ├── PageB (VIEW restriction: nationality NZ - our caller passes)
/// │   ├── PageC (EDIT restriction: group "senior" - existence visible, contents not)
/// │   └── PageD (VIEW restriction: nationality US - a protected leaf for our NZ caller)
/// </code>
/// </summary>
public sealed class PageTreeRestrictionMarkerTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(
        Guid SpaceId, Guid PageAId, Guid PageBId, Guid PageCId, Guid PageDId,
        Guid ViewRuleId, string ViewRuleExpressionJson, Guid EditRuleId, Guid UsOnlyRuleId);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var space = new Space
        {
            Key = $"TRM{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Tree Marker Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.AccessGrant,
            SpaceId = space.Id,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        });

        var pageA = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "a", Title = "Page A", SortOrder = 0, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.Add(pageA);
        await db.SaveChangesAsync();

        Page Child(string slug, string title, int sortOrder) => new()
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = slug, Title = title, SortOrder = sortOrder, CreatedAtUtc = now, UpdatedAtUtc = now,
        };

        var pageB = Child("b", "Page B (view-restricted, passable)", 0);
        var pageC = Child("c", "Page C (edit-restricted)", 1);
        var pageD = Child("d", "Page D (view-restricted, unpassable)", 2);
        db.Pages.AddRange(pageB, pageC, pageD);
        await db.SaveChangesAsync();

        AccessRule Restriction(Guid pageId, PageAction action, RuleNode expression)
        {
            var rule = new AccessRule
            {
                Kind = AccessRuleKind.PageRestriction,
                PageId = pageId,
                Action = action,
                ExpressionJson = RuleExpressionSerializer.Serialize(expression),
                CreatedAtUtc = now,
                CreatedByUserId = creator.Id,
                UpdatedAtUtc = now,
                UpdatedByUserId = creator.Id,
            };
            db.AccessRules.Add(rule);
            return rule;
        }

        var viewRule = Restriction(pageB.Id, PageAction.View, new AttrCondition("nationality", ["NZ"]));
        var editRule = Restriction(pageC.Id, PageAction.Edit, new GroupCondition("senior"));
        var usOnlyRule = Restriction(pageD.Id, PageAction.View, new AttrCondition("nationality", ["US"]));
        await db.SaveChangesAsync();

        return new Fixture(
            space.Id, pageA.Id, pageB.Id, pageC.Id, pageD.Id,
            viewRule.Id, viewRule.ExpressionJson, editRule.Id, usOnlyRule.Id);
    }

    private const string NodeFields = "id title hasRestrictions ownViewRestrictions { ruleId expressionJson }";

    private async Task<(JsonDocument Doc, string Body)> QueryTreeAsync(Guid spaceId)
    {
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"trm-{Guid.NewGuid()}", nationality: ["NZ"]);
        var doc = await client.PostGraphQLAsync($$"""
            { pageTree(spaceId: "{{spaceId}}") {
                ... on PageTreeNode {
                  {{NodeFields}}
                  children {
                    ... on PageTreeNode { {{NodeFields}} }
                    ... on ProtectedTreeNode {
                      title sortOrder
                      denial { placeholderTitle noSpaceAccess marking { label } reasons { gate passed ruleId inherited } }
                    }
                  }
                }
              } }
            """);
        return (doc, doc.RootElement.ToString());
    }

    private static JsonElement Root(JsonDocument doc) =>
        Assert.Single(doc.RootElement.GetProperty("data").GetProperty("pageTree").EnumerateArray());

    private static JsonElement ChildById(JsonDocument doc, Guid id) =>
        Root(doc).GetProperty("children").EnumerateArray()
            .Single(c => c.TryGetProperty("id", out var childId)
                && string.Equals(childId.GetString(), id.ToString(), StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task HasRestrictions_TrueOnlyWhereARuleActuallySits()
    {
        var f = await SeedAsync();
        var (doc, _) = await QueryTreeAsync(f.SpaceId);

        Assert.False(Root(doc).GetProperty("hasRestrictions").GetBoolean(), "PageA carries no rule of its own.");
        Assert.True(ChildById(doc, f.PageBId).GetProperty("hasRestrictions").GetBoolean());
        Assert.True(ChildById(doc, f.PageCId).GetProperty("hasRestrictions").GetBoolean(),
            "An edit-only restriction still lights the lock badge (§6.6).");
    }

    [Fact]
    public async Task OwnViewRestrictions_CarryTheExactRuleIdAndExpression_ForRulesTheCallerPassed()
    {
        // What the move dialog consumes (web/src/access/move): rule id for chain
        // diffing, expression for display - safe here because the caller passed it.
        var f = await SeedAsync();
        var (doc, _) = await QueryTreeAsync(f.SpaceId);

        var restriction = Assert.Single(ChildById(doc, f.PageBId).GetProperty("ownViewRestrictions").EnumerateArray());
        Assert.Equal(f.ViewRuleId.ToString(), restriction.GetProperty("ruleId").GetString(), ignoreCase: true);
        Assert.Equal(f.ViewRuleExpressionJson, restriction.GetProperty("expressionJson").GetString());
    }

    [Fact]
    public async Task EditRuleContents_NeverAppearInTheTree_OnlyTheBoolean()
    {
        var f = await SeedAsync();
        var (doc, body) = await QueryTreeAsync(f.SpaceId);

        Assert.Empty(ChildById(doc, f.PageCId).GetProperty("ownViewRestrictions").EnumerateArray());
        Assert.DoesNotContain(f.EditRuleId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("senior", body);
    }

    [Fact]
    public async Task ProtectedNode_LeaksNeitherItsIdNorItsRuleExpression()
    {
        // The §21.8 regression guard for the placeholder: PageD is DISCLOSED - a
        // ProtectedTreeNode at its sibling position, with its marking and the rule id
        // the audit row would name - and what it must never carry is exactly what
        // identifies the page or its protected audience: the page id, the rule's
        // expression, and the distinctive allowed-value inside it.
        var f = await SeedAsync();
        var (doc, body) = await QueryTreeAsync(f.SpaceId);

        var placeholder = Assert.Single(
            Root(doc).GetProperty("children").EnumerateArray(),
            c => c.TryGetProperty("denial", out _));
        Assert.Equal(AccessDenialView.ProtectedTitle, placeholder.GetProperty("title").GetString());
        Assert.Equal(2, placeholder.GetProperty("sortOrder").GetInt32());
        var denial = placeholder.GetProperty("denial");
        Assert.False(denial.GetProperty("noSpaceAccess").GetBoolean());
        Assert.Equal("UK OFFICIAL", denial.GetProperty("marking").GetProperty("label").GetString());

        var reason = Assert.Single(denial.GetProperty("reasons").EnumerateArray());
        Assert.Equal("RESTRICTION", reason.GetProperty("gate").GetString());
        Assert.False(reason.GetProperty("passed").GetBoolean());
        Assert.Equal(f.UsOnlyRuleId.ToString(), reason.GetProperty("ruleId").GetString(), ignoreCase: true);
        Assert.False(reason.GetProperty("inherited").GetBoolean(), "The rule sits on PageD itself.");

        Assert.DoesNotContain(f.PageDId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        // The pruned rule's distinctive allowed-value never appears (no title, label,
        // gate name or expression in this fixture legitimately contains the uppercase
        // token - the placeholder's own vocabulary was chosen to keep it that way).
        Assert.DoesNotContain("US", body);
    }
}
