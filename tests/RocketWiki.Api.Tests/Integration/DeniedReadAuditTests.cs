using System.Net;
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
/// design.md §6.7's second sentence, end to end: "indistinguishable to the caller,
/// not to the audit log". PageAdversarialLeakTests proves the caller half (a
/// restricted page is absent through every path); this class proves the other half -
/// a denied read lands in the audit table with Outcome.Denied and the specific
/// failing restriction (§7's "denied requests are recorded along with which
/// restriction failed"), while a genuinely-missing subject audits nothing, an
/// anonymous request audits nothing, and - the part that keeps both halves true at
/// once - the denied and missing HTTP responses stay byte-identical.
///
/// Fixture mirrors the leak tests: a space every principal can view, PageA
/// (permitted) with a child PageB restricted to nationality US, exercised by an NZ
/// principal.
/// </summary>
public sealed class DeniedReadAuditTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    private sealed record Fixture(Guid SpaceId, Guid PageAId, Guid PageBId, Guid RestrictionRuleId, Guid CreatorId);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();

        var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var space = new Space
        {
            Key = $"DRA{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Denied Read Audit Space",
            OriginInstanceId = "standalone",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
        };
        db.Spaces.Add(space);
        db.AccessRules.Add(new AccessRule
        {
            Kind = AccessRuleKind.SpaceGrant,
            SpaceId = space.Id,
            Role = SpaceRole.Viewer,
            ExpressionJson = RuleExpressionSerializer.Serialize(new EveryoneCondition()),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        });

        var now = DateTime.UtcNow;
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
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = creator.Id,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedByUserId = creator.Id,
        };
        db.AccessRules.Add(restriction);
        await db.SaveChangesAsync();

        return new Fixture(space.Id, pageA.Id, pageB.Id, restriction.Id, creator.Id);
    }

    private async Task<List<AuditEvent>> AuditRowsForSubjectAsync(Guid subjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents.Where(e => e.SubjectId == subjectId).ToListAsync();
    }

    private async Task<int> TotalAuditRowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        return await db.AuditEvents.CountAsync();
    }

    [Fact]
    public async Task DeniedPageRead_IsAudited_WithTheFailingRestrictionReason()
    {
        var f = await SeedAsync();
        var sub = $"nz-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.SetTestUser(sub: sub, nationality: ["NZ"]);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageBId}}") { id title } }""");

        // The caller still sees nothing (PageAdversarialLeakTests owns the exhaustive
        // version of this; asserted here so the audit row below provably came from a
        // response that leaked nothing).
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);

        var rows = await AuditRowsForSubjectAsync(f.PageBId);
        var denial = Assert.Single(rows);
        Assert.Equal("page.view", denial.Action);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(AuditSubjectType.Page, denial.SubjectType);
        Assert.Equal(AuditChannel.GraphQl, denial.Channel);

        // design.md §15's convention: the audit row keeps the *specific*
        // restriction:{pageId}:{ruleId} reason (telemetry only ever gets the bounded
        // category, asserted by TelemetryHygieneTests) - and the same {"reason": ...}
        // details shape the mutation-denial path writes.
        var expectedReason = $"restriction:{f.PageBId}:{f.RestrictionRuleId}";
        Assert.NotNull(denial.DetailsJson);
        var details = JsonDocument.Parse(denial.DetailsJson);
        Assert.Equal(expectedReason, details.RootElement.GetProperty("reason").GetString());
        // Never the rule's expression or the principal's attribute values (§15's example
        // of exactly what must not spread) - ids only.
        Assert.DoesNotContain("nationality", denial.DetailsJson);

        // Attributed to the JIT-provisioned acting user, not anonymous/system.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
        var actingUser = await db.Users.SingleAsync(u => u.Subject == sub);
        Assert.Equal(actingUser.Id, denial.UserId);
    }

    [Fact]
    public async Task PermittedPageRead_StillAuditsSuccess_NotSuppressedByTheDenialRule()
    {
        // Positive control for DbAuditSink's denied-suppresses-success asymmetry: a
        // request with no denial in it must keep producing its normal Success row.
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"us-{Guid.NewGuid()}", nationality: ["US"]);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageBId}}") { id } }""");
        Assert.Equal(f.PageBId.ToString(), result.RootElement.GetProperty("data").GetProperty("page").GetProperty("id").GetString(), ignoreCase: true);

        var rows = await AuditRowsForSubjectAsync(f.PageBId);
        var success = Assert.Single(rows);
        Assert.Equal("page.view", success.Action);
        Assert.Equal(AuditOutcome.Success, success.Outcome);
    }

    [Fact]
    public async Task MissingPageRead_AuditsNothing()
    {
        // Design call, stated in ReadDenialAudit's doc: §7's outcome vocabulary is
        // success|denied. A read of a page that doesn't exist made no access decision
        // (nothing existed to decide about), so recording Denied would flood the
        // probing signal the permission inspector feeds on with plain-404 noise, and
        // recording Success would claim a read that never happened. NotFound therefore
        // audits nothing - same as the pre-existing, tested behavior for null results.
        await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);
        var missingId = Guid.NewGuid();

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{missingId}}") { id title } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        Assert.Empty(await AuditRowsForSubjectAsync(missingId));
    }

    [Fact]
    public async Task AnonymousRequest_StillNull_AndAuditsNothing()
    {
        // Unchanged behavior: no Principal means the resolver returns null before the
        // read service ever runs - no denial was computed, and DbAuditSink would (by
        // design) throw rather than write an anonymous-looking row anyway.
        var f = await SeedAsync();
        var client = factory.CreateClient(); // no SetTestUser - anonymous
        var rowsBefore = await TotalAuditRowsAsync();

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageBId}}") { id title } }""");

        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("data").GetProperty("page").ValueKind);
        Assert.Equal(rowsBefore, await TotalAuditRowsAsync());
    }

    [Fact]
    public async Task DeniedAndMissing_PageResponses_AreByteIdentical_AtTheHttpBoundary()
    {
        // The response-shape half of §6.7, proven at the transport rather than via
        // parsed JSON: same status, same content type, and - because both ids are
        // interpolated into the request but never echoed into the response - the exact
        // same body bytes. Any future divergence (a typed error, an extensions hint, a
        // timing-independent shape change) fails here.
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        var deniedResponse = await client.PostAsJsonAsync("/graphql", new { query = $$"""{ page(id: "{{f.PageBId}}") { id title } }""" });
        var missingResponse = await client.PostAsJsonAsync("/graphql", new { query = $$"""{ page(id: "{{Guid.NewGuid()}}") { id title } }""" });

        Assert.Equal(missingResponse.StatusCode, deniedResponse.StatusCode);
        Assert.Equal(missingResponse.Content.Headers.ContentType?.ToString(), deniedResponse.Content.Headers.ContentType?.ToString());
        Assert.Equal(await missingResponse.Content.ReadAsStringAsync(), await deniedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PrunedChildOfPermittedParent_IsNotADeniedRequest_AuditsNoDenial()
    {
        // Pruning is not denial (IPageReadService.GetPageTreeAsync's contract): asking
        // for PageA's children succeeds and simply doesn't include PageB, exactly as it
        // wouldn't for anyone - recording a Denied for every pruned node would turn
        // ordinary browsing into a firehose of false "probing" signals for pages the
        // caller never asked about. Only a *directly requested* subject can be denied.
        var f = await SeedAsync();
        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);

        var result = await client.PostGraphQLAsync($$"""{ page(id: "{{f.PageAId}}") { children { id } } }""");

        var childIds = result.RootElement.GetProperty("data").GetProperty("page").GetProperty("children")
            .EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.DoesNotContain(f.PageBId.ToString(), childIds, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(await AuditRowsForSubjectAsync(f.PageBId));
    }

    [Fact]
    public async Task DeniedTreeBrowse_NoSpaceRole_AuditsDenied_AndNeverAlsoSuccess()
    {
        // A space with no grants at all: ComputeSpaceRole is null, so the browse itself
        // is refused (Denied "no-space-role") - unlike pruning above, the *request* was
        // denied. The caller still sees the same empty list as a nonexistent space; the
        // audit log gets exactly one Denied row and - the DbAuditSink asymmetry -
        // AuditFieldMiddleware's would-be Success row for the same subject is
        // suppressed, so a refused browse never also claims success.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
            db.Users.Add(creator);
            var space = new Space
            {
                Key = $"NGR{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "No Grants Space",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
            };
            db.Spaces.Add(space);
            await db.SaveChangesAsync();

            var client = factory.CreateClient();
            client.SetTestUser(sub: $"roleless-{Guid.NewGuid()}");

            var deniedResponse = await client.PostAsJsonAsync("/graphql", new { query = $$"""{ pageTree(spaceId: "{{space.Id}}") { id } }""" });
            var missingResponse = await client.PostAsJsonAsync("/graphql", new { query = $$"""{ pageTree(spaceId: "{{Guid.NewGuid()}}") { id } }""" });

            // Caller-indistinguishable from a space that doesn't exist, byte for byte.
            Assert.Equal(HttpStatusCode.OK, deniedResponse.StatusCode);
            Assert.Equal(await missingResponse.Content.ReadAsStringAsync(), await deniedResponse.Content.ReadAsStringAsync());

            var rows = await AuditRowsForSubjectAsync(space.Id);
            var denial = Assert.Single(rows);
            Assert.Equal("space.browse", denial.Action);
            Assert.Equal(AuditOutcome.Denied, denial.Outcome);
            Assert.Equal(AuditSubjectType.Space, denial.SubjectType);
            Assert.NotNull(denial.DetailsJson);
            Assert.Equal("no-space-role", JsonDocument.Parse(denial.DetailsJson).RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task DeniedSpaceLookup_NoSpaceRole_IsAudited_AndByteIdenticalToAMissingKey()
    {
        // The seam the denied-read work initially left open, now closed: space(key) is
        // a *specific-space* lookup, so a caller holding no role gets the same Denied
        // space.browse row a refused pageTree browse gets (same SpaceReads seam as
        // MCP's get_page_tree - see McpToolTests for that channel), while the response
        // stays byte-identical to a key that names nothing (§6.7). A genuinely-missing
        // key still audits nothing, and the listing shape (spaces) deliberately keeps
        // its no-per-item-audit behavior - a filtered listing is not a denial
        // (ReadDenialAudit's doc; PrunedChildOfPermittedParent_... above is the page
        // twin of that rule).
        Guid spaceId;
        string spaceKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var creator = new User { Subject = $"seed-{Guid.NewGuid()}", DisplayName = "Seeder", CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow };
            db.Users.Add(creator);
            var space = new Space
            {
                Key = $"NSR{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
                Name = "No Role Space",
                OriginInstanceId = "standalone",
                CreatedAtUtc = DateTime.UtcNow,
                CreatedByUserId = creator.Id,
            };
            db.Spaces.Add(space); // no grants at all - ComputeSpaceRole is null for everyone
            await db.SaveChangesAsync();
            spaceId = space.Id;
            spaceKey = space.Key;
        }

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"roleless-{Guid.NewGuid()}");
        var missingKey = $"MIS{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        var deniedResponse = await client.PostAsJsonAsync("/graphql", new { query = $$"""{ space(key: "{{spaceKey}}") { id name } }""" });

        // The missing-key control writes nothing at all (§7: no access decision
        // exists for a subject that isn't there) - checked by total row count since a
        // nonexistent space has no subject id to filter on.
        var rowsBeforeMissing = await TotalAuditRowsAsync();
        var missingResponse = await client.PostAsJsonAsync("/graphql", new { query = $$"""{ space(key: "{{missingKey}}") { id name } }""" });
        Assert.Equal(rowsBeforeMissing, await TotalAuditRowsAsync());

        // §6.7's response half at the transport: both keys are 8 chars, interpolated
        // into the request and never echoed back, so the bodies must match byte for byte.
        Assert.Equal(HttpStatusCode.OK, deniedResponse.StatusCode);
        Assert.Equal(missingResponse.StatusCode, deniedResponse.StatusCode);
        Assert.Equal(await missingResponse.Content.ReadAsStringAsync(), await deniedResponse.Content.ReadAsStringAsync());

        var rows = await AuditRowsForSubjectAsync(spaceId);
        var denial = Assert.Single(rows);
        Assert.Equal("space.browse", denial.Action);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(AuditSubjectType.Space, denial.SubjectType);
        Assert.Equal(AuditChannel.GraphQl, denial.Channel);
        Assert.NotNull(denial.DetailsJson);
        Assert.Equal("no-space-role", JsonDocument.Parse(denial.DetailsJson).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DeniedAttachmentDownload_IsAudited_AndByteIdenticalToAMissing404()
    {
        // Same contract on the attachment channel (design.md §8/§10): the service's
        // internal Denied is audited with the failing restriction and then collapses
        // to the exact 404 a nonexistent attachment id gets. The blob deliberately
        // doesn't exist in storage - canView is checked before the store is touched,
        // which this test also pins (a denial must never read the object store).
        var f = await SeedAsync();
        Guid attachmentId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RocketWikiDbContext>();
            var attachment = new Attachment
            {
                PageId = f.PageBId, // the restricted page
                FileName = "secret.bin",
                ContentType = "application/octet-stream",
                SizeBytes = 3,
                ContentHash = new byte[32],
                StorageKey = $"attachments/never-written/{Guid.NewGuid()}",
                UploadedByUserId = f.CreatorId,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
            attachmentId = attachment.Id;
        }

        var client = factory.CreateClient();
        client.SetTestUser(sub: $"nz-{Guid.NewGuid()}", nationality: ["NZ"]);
        var missingId = Guid.NewGuid();

        var deniedResponse = await client.GetAsync($"/attachments/{attachmentId}");
        var missingResponse = await client.GetAsync($"/attachments/{missingId}");

        Assert.Equal(HttpStatusCode.NotFound, deniedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(await missingResponse.Content.ReadAsByteArrayAsync(), await deniedResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(missingResponse.Content.Headers.ContentType?.ToString(), deniedResponse.Content.Headers.ContentType?.ToString());

        var deniedRows = await AuditRowsForSubjectAsync(attachmentId);
        var denial = Assert.Single(deniedRows);
        Assert.Equal("attachment.download", denial.Action);
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(AuditSubjectType.Attachment, denial.SubjectType);
        Assert.Equal(AuditChannel.Attachment, denial.Channel);
        Assert.NotNull(denial.DetailsJson);
        Assert.Equal(
            $"restriction:{f.PageBId}:{f.RestrictionRuleId}",
            JsonDocument.Parse(denial.DetailsJson).RootElement.GetProperty("reason").GetString());

        Assert.Empty(await AuditRowsForSubjectAsync(missingId));
    }
}
