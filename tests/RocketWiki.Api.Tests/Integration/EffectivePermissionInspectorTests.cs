using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Data;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// design.md §6.6's permission inspector at the API boundary: self-inspection for
/// anyone who can view the page, foreign inspection for instance admins only, the
/// caller's own canView as an absolute gate in every mode (§6.5 no-read-around),
/// and permission.inspect audit rows on success AND denial with the inspected
/// principal's id - never their supplied groups/attribute values - in the details
/// (§7). Fixture mirrors DeniedReadAuditTests:
///
/// <code>
/// Space (viewer: everyone, editor: "editors")
/// └── PageA (unrestricted)
///     └── PageB (VIEW restriction: nationality US)
/// </code>
/// </summary>
public sealed class EffectivePermissionInspectorTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(Guid SpaceId, Guid PageAId, Guid PageBId, Guid RestrictionRuleId);

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
            Key = $"EPI{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Inspector Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);

        void Grant(SpaceRole? role, RuleNode expression) => db.AccessRules.AddRange(TestAccessRules.WithAccessBesideRole(new AccessRule
        {
            Kind = role is null ? AccessRuleKind.AccessGrant : AccessRuleKind.RoleGrant,
            SpaceId = space.Id,
            Role = role,
            ExpressionJson = RuleExpressionSerializer.Serialize(expression),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        }));

        Grant(null, new EveryoneCondition()); // access only: may see, holds no role
        Grant(SpaceRole.Editor, new GroupCondition("editors"));

        var pageA = new Page { SpaceId = space.Id, AncestorPath = "/", Slug = "a", Title = "Page A", CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Pages.Add(pageA);
        await db.SaveChangesAsync();

        var pageB = new Page
        {
            SpaceId = space.Id, ParentPageId = pageA.Id, AncestorPath = $"/{pageA.Id}/",
            Slug = "b-restricted", Title = "Page B (restricted)", CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Pages.Add(pageB);
        await db.SaveChangesAsync();

        var restriction = new AccessRule
        {
            Kind = AccessRuleKind.PageRestriction,
            PageId = pageB.Id,
            Action = PageAction.View,
            ExpressionJson = RuleExpressionSerializer.Serialize(new AttrCondition("nationality", ["US"])),
            CreatedAtUtc = now,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = now,
            UpdatedByUserId = creator.Id,
        };
        db.AccessRules.Add(restriction);
        await db.SaveChangesAsync();

        return new Fixture(space.Id, pageA.Id, pageB.Id, restriction.Id);
    }

    private const string DetailSelection = """
        {
          userId userDisplayName spaceRole isReplicaSpace
          canView canEdit viewDenialReason editDenialReason
          viewRestrictions { ruleId pageId pageTitle action expressionJson passed }
          editRestrictions { ruleId passed }
        }
        """;

    private async Task<List<AuditEvent>> InspectAuditRowsAsync(Guid pageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents
            .Where(e => e.SubjectId == pageId && e.Action == "permission.inspect")
            .ToListAsync();
    }

    [Fact]
    public async Task SelfInspection_OnAViewablePage_ExplainsRoleVerdictAndReasonVocabulary()
    {
        var f = await SeedAsync();
        var sub = $"self-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, name: "Self Inspector", nationality: ["NZ"]);

        var result = await client.PostGraphQLAsync(
            $$"""{ effectivePermission(pageId: "{{f.PageAId}}") {{DetailSelection}} }""");

        var detail = result.RootElement.GetProperty("data").GetProperty("effectivePermission");
        Assert.Equal(sub, detail.GetProperty("userId").GetString());
        // Display name comes from the JIT-provisioned mirror row for a real user.
        Assert.Equal("Self Inspector", detail.GetProperty("userDisplayName").GetString());
        // An access grant confers no role (design.md §6.4); the inspector reports none.
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("spaceRole").ValueKind);
        Assert.False(detail.GetProperty("isReplicaSpace").GetBoolean());
        Assert.True(detail.GetProperty("canView").GetBoolean());
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("viewDenialReason").ValueKind);
        // The exact vocabulary web/src/access/permission/describeDenialReason.ts parses.
        Assert.Equal("insufficient-space-role", detail.GetProperty("editDenialReason").GetString());
        Assert.Empty(detail.GetProperty("viewRestrictions").EnumerateArray());

        var row = Assert.Single(await InspectAuditRowsAsync(f.PageAId));
        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.NotNull(row.DetailsJson);
        var details = JsonDocument.Parse(row.DetailsJson);
        Assert.Equal(sub, details.RootElement.GetProperty("inspectedUserId").GetString());
        Assert.True(details.RootElement.GetProperty("self").GetBoolean());
    }

    [Fact]
    public async Task SelfInspection_OnAViewablePage_ListsViewAndEditGates()
    {
        // design.md §6.6/§21.2: the whole ladder, every gate with its pass/fail, in the
        // same GateResult shape a placeholder's reasons use (§21.8). PageA for an
        // access-only caller: S, C and N pass (no selectors, no restrictions on A), and
        // the edit half is replica (passed) then role (failed - no role grant matched).
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"gates-{Guid.NewGuid()}", nationality: ["NZ"]);

        var result = await client.PostGraphQLAsync($$"""
            { effectivePermission(pageId: "{{f.PageAId}}") {
                hasSpaceAccess spaceRole canView canEdit
                viewGates { gate passed ruleId inherited }
                editGates { gate passed requiredRole }
              } }
            """);

        var detail = result.RootElement.GetProperty("data").GetProperty("effectivePermission");
        Assert.True(detail.GetProperty("hasSpaceAccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("spaceRole").ValueKind);
        Assert.Equal(
            ["SPACE_ACCESS", "CLASSIFICATION", "NATIONAL_CAVEAT"],
            detail.GetProperty("viewGates").EnumerateArray().Select(g => g.GetProperty("gate").GetString()));
        Assert.All(detail.GetProperty("viewGates").EnumerateArray(), g => Assert.True(g.GetProperty("passed").GetBoolean()));

        var editGates = detail.GetProperty("editGates").EnumerateArray().ToList();
        Assert.Equal(["REPLICA", "ROLE"], editGates.Select(g => g.GetProperty("gate").GetString()));
        Assert.True(editGates[0].GetProperty("passed").GetBoolean());
        Assert.False(editGates[1].GetProperty("passed").GetBoolean());
        Assert.Equal("EDITOR", editGates[1].GetProperty("requiredRole").GetString());

        // A US editor on PageB: the restriction appears as a passed RESTRICTION gate
        // carrying its rule id and sitting on the page itself (not inherited).
        var editor = factory.CreateClient();
        editor.SetTestUser(sub: $"gates-editor-{Guid.NewGuid()}", groups: ["editors"], nationality: ["US"]);
        var onB = await editor.PostGraphQLAsync($$"""
            { effectivePermission(pageId: "{{f.PageBId}}") { spaceRole canEdit viewGates { gate passed ruleId inherited } editGates { gate passed } } }
            """);
        var detailB = onB.RootElement.GetProperty("data").GetProperty("effectivePermission");
        Assert.Equal("EDITOR", detailB.GetProperty("spaceRole").GetString());
        Assert.True(detailB.GetProperty("canEdit").GetBoolean());
        var restriction = Assert.Single(detailB.GetProperty("viewGates").EnumerateArray(), g => g.GetProperty("gate").GetString() == "RESTRICTION");
        Assert.True(restriction.GetProperty("passed").GetBoolean());
        Assert.Equal(f.RestrictionRuleId.ToString(), restriction.GetProperty("ruleId").GetString(), ignoreCase: true);
        Assert.False(restriction.GetProperty("inherited").GetBoolean());
        Assert.All(detailB.GetProperty("editGates").EnumerateArray(), g => Assert.True(g.GetProperty("passed").GetBoolean()));
    }

    [Fact]
    public async Task SelfInspection_OnAPageTheCallerCannotView_IsNullLikeMissing_AndAuditedDenied()
    {
        // The inspector must not become the leak §6.7 forbids: aimed at a page you
        // can't view it would confirm existence AND hand over the failing rule.
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        var denied = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ effectivePermission(pageId: "{{f.PageBId}}") { canView } }""",
        });
        var missing = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ effectivePermission(pageId: "{{Guid.NewGuid()}}") { canView } }""",
        });

        // Byte-identical at the HTTP boundary, same as denied-vs-missing page reads.
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await denied.Content.ReadAsStringAsync());

        var row = Assert.Single(await InspectAuditRowsAsync(f.PageBId));
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal($"restriction:{f.PageBId}:{f.RestrictionRuleId}",
            details.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task MissingPage_IsNull_AndAuditsNothing()
    {
        await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);
        var missingId = Guid.NewGuid();

        var result = await client.PostGraphQLAsync(
            $$"""{ effectivePermission(pageId: "{{missingId}}") { canView } }""");

        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("effectivePermission").ValueKind);
        Assert.Empty(await InspectAuditRowsAsync(missingId));
    }

    [Fact]
    public async Task NonAdmin_InspectingAnotherPrincipal_IsRefusedBeforeAnyPageLookup_AndAudited()
    {
        // Foreign inspection is an admin diagnostic. A non-admin gets null - decided
        // before the page is even looked up, so the null confirms nothing about the
        // page id - and the probe lands in the audit log (§7 "makes probing visible").
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nonadmin-{Guid.NewGuid()}", nationality: ["US"]);

        var result = await client.PostGraphQLAsync($$"""
            { effectivePermission(pageId: "{{f.PageAId}}", subject: { userId: "someone-else" }) { canView } }
            """);

        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("effectivePermission").ValueKind);

        var row = Assert.Single(await InspectAuditRowsAsync(f.PageAId));
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal("instance-admin-required", details.RootElement.GetProperty("reason").GetString());
        Assert.Equal("someone-else", details.RootElement.GetProperty("inspectedUserId").GetString());
        Assert.False(details.RootElement.GetProperty("self").GetBoolean());
    }

    [Fact]
    public async Task Admin_InspectingAForeignPrincipal_GetsTheFullNonShortCircuitedBreakdown()
    {
        // A US admin (passes PageB's restriction) inspects a hypothetical NZ subject
        // (fails it): the detail must show the failing check AND the audit row must
        // carry only the subject's id - never the nationality values the input
        // supplied (§6.2 marks attribute values sensitive; §7 sanctions rule
        // contents, not a person's attributes).
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"admin-{Guid.NewGuid()}", nationality: ["US"], roles: ["admin"]);

        var result = await client.PostGraphQLAsync($$"""
            { effectivePermission(
                pageId: "{{f.PageBId}}",
                subject: {
                  userId: "subject-under-test"
                  groups: ["editors"]
                  attributes: [{ key: "nationality", values: ["NZ"] }]
                }) {{DetailSelection}} }
            """);

        var detail = result.RootElement.GetProperty("data").GetProperty("effectivePermission");
        Assert.Equal("subject-under-test", detail.GetProperty("userId").GetString());
        // A what-if principal has no mirror row; the id doubles as display name.
        Assert.Equal("subject-under-test", detail.GetProperty("userDisplayName").GetString());
        Assert.Equal("EDITOR", detail.GetProperty("spaceRole").GetString());
        Assert.False(detail.GetProperty("canView").GetBoolean());
        Assert.Equal($"restriction:{f.PageBId}:{f.RestrictionRuleId}".ToLowerInvariant(),
            detail.GetProperty("viewDenialReason").GetString()!.ToLowerInvariant());

        var check = Assert.Single(detail.GetProperty("viewRestrictions").EnumerateArray());
        Assert.Equal(f.RestrictionRuleId.ToString(), check.GetProperty("ruleId").GetString(), ignoreCase: true);
        Assert.Equal("Page B (restricted)", check.GetProperty("pageTitle").GetString());
        Assert.False(check.GetProperty("passed").GetBoolean());
        Assert.Contains("nationality", check.GetProperty("expressionJson").GetString());

        var row = Assert.Single(await InspectAuditRowsAsync(f.PageBId));
        Assert.Equal(AuditOutcome.Success, row.Outcome);
        Assert.NotNull(row.DetailsJson);
        Assert.Contains("subject-under-test", row.DetailsJson);
        Assert.DoesNotContain("NZ", row.DetailsJson);
        Assert.DoesNotContain("nationality", row.DetailsJson);
        Assert.DoesNotContain("editors", row.DetailsJson);
    }

    [Fact]
    public async Task Admin_CannotUseTheInspector_AsAReadAroundOntoAPageTheyCannotView()
    {
        // §6.5: no silent read-around, inspector included. An NZ admin fails PageB's
        // restriction themselves, so inspecting ANY subject on it is null-like-missing
        // - the sanctioned path is to change the rules (audited) and then inspect.
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"admin-nz-{Guid.NewGuid()}", nationality: ["NZ"], roles: ["admin"]);

        var result = await client.PostGraphQLAsync($$"""
            { effectivePermission(pageId: "{{f.PageBId}}", subject: { userId: "anyone" }) {
                canView viewRestrictions { expressionJson } } }
            """);

        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("effectivePermission").ValueKind);

        var row = Assert.Single(await InspectAuditRowsAsync(f.PageBId));
        Assert.Equal(AuditOutcome.Denied, row.Outcome);
        var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal($"restriction:{f.PageBId}:{f.RestrictionRuleId}",
            details.RootElement.GetProperty("reason").GetString());
        Assert.Equal("anyone", details.RootElement.GetProperty("inspectedUserId").GetString());
    }

    [Fact]
    public async Task Anonymous_GetsNull_AndAuditsNothing()
    {
        var f = await SeedAsync();
        var client = factory.CreateClient(); // no SetTestUser

        var result = await client.PostGraphQLAsync(
            $$"""{ effectivePermission(pageId: "{{f.PageAId}}") { canView } }""");

        Assert.Equal(JsonValueKind.Null,
            result.RootElement.GetProperty("data").GetProperty("effectivePermission").ValueKind);
        Assert.Empty(await InspectAuditRowsAsync(f.PageAId));
    }
}
