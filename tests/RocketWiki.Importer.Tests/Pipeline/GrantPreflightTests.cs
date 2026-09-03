using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Tests.Pipeline.Fakes;

namespace RocketWiki.Importer.Tests.Pipeline;

/// <summary>
/// The initial space grant is not only who may read the space afterwards — it is also the
/// importer's own write permission for the whole run, because every service call is made
/// as the importer principal.
///
/// <para>A grant the importer cannot satisfy therefore created the space and then refused
/// all 250 pages, and the run still reported success: "Import complete. 0 of 250 page(s)
/// imported; 250 skipped", leaving an empty space whose key is now taken and a re-run
/// blocked. Two configurations did it — a sub-admin role grant, and an attribute-based grant
/// against a principal with no attributes, which is the natural ABAC shape for an
/// export-control org. Neither said what was wrong, and the way out that always works is
/// <c>{"everyone": true}</c>, which design.md §6.5.1 exists to prevent. Refusing before
/// the space key is consumed is the whole fix.</para>
/// </summary>
public class GrantPreflightTests
{
    private readonly FakeSpaceService _spaceService = new();
    private readonly FakePageService _pageService = new();
    private readonly FakeAttachmentService _attachmentService = new();
    private readonly FakeCommentService _commentService = new();
    private readonly FakeLabelService _labelService = new();

    private ConfluenceSpaceImporter CreateImporter() =>
        new(_spaceService, _pageService, _attachmentService, _commentService, _labelService);

    private static ConfluenceExportSpace Export() =>
        new("ENG", "Engineering", null,
            [new ConfluenceExportPage("1", null, "Home", "<p>Hi</p>", null, null, [])]);

    private static ImportOptions OptionsWith(Principal principal, SpaceRole role, string expressionJson) =>
        new(principal, Guid.NewGuid(),
            new AuditContext(AuditChannel.System, "test-import", "127.0.0.1"),
            new InitialGrant(AccessRuleKind.RoleGrant, role, expressionJson));

    [Fact]
    public async Task An_editor_grant_is_refused_before_the_space_key_is_taken()
    {
        // design.md §6.5.1: a space is born with a space-admin role grant, and "viewer" is
        // no longer a role at all - it is an access grant, which the importer adds beside
        // the role grant on its own.
        var options = OptionsWith(Principal.Create("importer-sub", ["engineering"]),
            SpaceRole.Editor, """{ "group": "engineering" }""");

        var result = await CreateImporter().ImportAsync(Export(), options);

        Assert.False(result.Success);
        Assert.Contains("space-admin", result.BlockedReason, StringComparison.Ordinal);

        // Nothing was created, so the operator can fix the flag and re-run the same
        // command. Previously the key was gone and the space had to be deleted first.
        Assert.Empty(_spaceService.InitialGrants);
        Assert.Empty(_pageService.CreatedPages);
    }

    [Fact]
    public async Task An_attribute_grant_the_importer_cannot_satisfy_is_refused_with_what_it_actually_has()
    {
        // The export-control shape: the grant everyone would reach for, against a
        // principal carrying no attributes at all. Fails closed on every page (§6.1),
        // and said nothing about why.
        var options = OptionsWith(Principal.Create("importer-sub", ["confluence-importer"]),
            SpaceRole.SpaceAdmin, """{ "attr": "nationality", "in": ["GBR"] }""");

        var result = await CreateImporter().ImportAsync(Export(), options);

        Assert.False(result.Success);
        Assert.Contains("does not satisfy the initial space grant", result.BlockedReason, StringComparison.Ordinal);
        // Naming what the principal HAS is what makes this fixable rather than just refused.
        Assert.Contains("confluence-importer", result.BlockedReason, StringComparison.Ordinal);
        Assert.Contains("attributes [(none)]", result.BlockedReason, StringComparison.Ordinal);
        Assert.Empty(_spaceService.InitialGrants);
    }

    [Fact]
    public async Task A_malformed_grant_expression_is_refused_rather_than_silently_denying_every_page()
    {
        var options = OptionsWith(Principal.Create("importer-sub", ["engineering"]),
            SpaceRole.SpaceAdmin, """{ "nonsense": true }""");

        var result = await CreateImporter().ImportAsync(Export(), options);

        Assert.False(result.Success);
        Assert.Contains("could not be parsed", result.BlockedReason, StringComparison.Ordinal);
        Assert.Empty(_spaceService.InitialGrants);
    }

    [Fact]
    public async Task A_grant_the_importer_satisfies_proceeds_normally()
    {
        // The non-vacuity half: the pre-flight must not refuse a workable import.
        var options = OptionsWith(Principal.Create("importer-sub", ["engineering"]),
            SpaceRole.SpaceAdmin, """{ "group": "engineering" }""");

        var result = await CreateImporter().ImportAsync(Export(), options);

        Assert.True(result.Success);
        Assert.Single(_spaceService.InitialGrants);
        Assert.Single(_pageService.CreatedPages);
    }

    [Fact]
    public async Task An_attribute_grant_the_importer_does_satisfy_proceeds()
    {
        // Proves the refusal is about the principal failing the rule, not about attr
        // rules being rejected as a shape — attr grants are the point of the model.
        var principal = Principal.Create("importer-sub", ["confluence-importer"],
            new Dictionary<string, IReadOnlyList<string>> { ["nationality"] = ["GBR"] });
        var options = OptionsWith(principal, SpaceRole.SpaceAdmin, """{ "attr": "nationality", "in": ["GBR"] }""");

        var result = await CreateImporter().ImportAsync(Export(), options);

        Assert.True(result.Success);
        Assert.Single(_pageService.CreatedPages);
    }
    [Fact]
    public async Task AnImportThatLandsNoPagesAtAll_IsNotReportedAsSuccess()
    {
        // "The space was created" is not "the import worked". This used to report
        // Success unconditionally, printing "Import complete" over a run that imported
        // nothing — with exit code 3 as the only hint, and 3 is the ordinary outcome for
        // a real migration, so it hints at nothing.
        _pageService.FailCreateWhen = _ => new ValidationError("nope");

        var options = OptionsWith(Principal.Create("importer-sub", ["engineering"]),
            SpaceRole.SpaceAdmin, """{ "group": "engineering" }""");

        var result = await CreateImporter().ImportAsync(Export(), options);

        Assert.False(result.Success);
        Assert.Contains("none of its 1 page(s) imported", result.BlockedReason, StringComparison.Ordinal);

        // The space DID get created, and the message says so — the operator has to delete
        // it before re-running, and that is the actionable part.
        Assert.NotNull(result.SpaceId);
        Assert.Single(_spaceService.InitialGrants);
    }
}
