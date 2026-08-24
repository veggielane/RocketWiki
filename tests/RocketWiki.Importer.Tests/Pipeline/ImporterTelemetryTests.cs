using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using RocketWiki.Core.Access;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Events;
using RocketWiki.Core.Services;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline;
using RocketWiki.Importer.Telemetry;
using RocketWiki.Importer.Tests.Pipeline.Fakes;

namespace RocketWiki.Importer.Tests.Pipeline;

/// <summary>
/// design.md §15/§16: the import pipeline's stage spans, proven to emit against the same
/// fake-service harness <see cref="ConfluenceSpaceImporterTests"/> uses.
///
/// The last test is the §15 one: a Confluence export is nothing but page titles, bodies
/// and author names, so this pipeline handles more content per second than any other
/// code path in the system. None of it may reach a span.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public class ImporterTelemetryTests
{
    private readonly FakeSpaceService _spaceService = new();
    private readonly FakePageService _pageService = new();
    private readonly FakeAttachmentService _attachmentService = new();
    private readonly FakeCommentService _commentService = new();
    private readonly FakeLabelService _labelService = new();

    private ConfluenceSpaceImporter CreateImporter() =>
        new(_spaceService, _pageService, _attachmentService, _commentService, _labelService);

    private static ImportOptions DefaultOptions() => new(
        Principal.Create("importer-sub", ["importers"]),
        Guid.NewGuid(),
        new AuditContext(AuditChannel.System, "test-import", "127.0.0.1"),
        new InitialSpaceGrant(SpaceRole.SpaceAdmin, """{ "everyone": true }"""));

    private static ConfluenceExportPage Page(string id, string? parentId, string title, string body,
        IReadOnlyList<ConfluenceExportAttachment>? attachments = null) =>
        new(id, parentId, title, body, null, CreatedAtUtc: null, attachments ?? []);

    private static (ActivityListener Listener, List<Activity> Captured) ListenToImporter()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ImporterTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, captured);
    }

    private static ConfluenceExportSpace SampleExport(string title1 = "Home", string title2 = "Details") =>
        new("ENG", "Engineering", "Engineering docs",
        [
            Page("1", null, title1, "<p>Body one.</p>",
            [
                new ConfluenceExportAttachment("a1", "diagram.png", "image/png", () => new MemoryStream([1, 2, 3])),
            ]),
            Page("2", "1", title2, "<p>Body two.</p>"),
        ]);

    [Fact]
    public async Task ImportAsync_EmitsASpanPerPipelineStageUnderOneImportSpan()
    {
        var (listener, captured) = ListenToImporter();
        using (listener)
        {
            var result = await CreateImporter().ImportAsync(SampleExport(), DefaultOptions());
            Assert.True(result.Success);
        }

        var names = captured.Select(a => a.OperationName).ToList();
        Assert.Contains(ImporterTelemetry.CreatePagesSpan, names);
        Assert.Contains(ImporterTelemetry.UploadAttachmentsSpan, names);
        Assert.Contains(ImporterTelemetry.ConvertContentSpan, names);
        Assert.Contains(ImporterTelemetry.ImportSpaceSpan, names);

        // The three passes nest under the import span, so a trace view shows where a
        // long-running migration actually is.
        var import = captured.Single(a => a.OperationName == ImporterTelemetry.ImportSpaceSpan);
        foreach (var pass in captured.Where(a => a.OperationName != ImporterTelemetry.ImportSpaceSpan))
        {
            Assert.Equal(import.SpanId, pass.ParentSpanId);
        }
    }

    [Fact]
    public async Task StageSpansCarryTheCountsThatSayHowFarAnImportGot()
    {
        var (listener, captured) = ListenToImporter();
        using (listener)
        {
            Assert.True((await CreateImporter().ImportAsync(SampleExport(), DefaultOptions())).Success);
        }

        var import = captured.Single(a => a.OperationName == ImporterTelemetry.ImportSpaceSpan);
        Assert.Equal("ENG", import.GetTagItem(ImporterTelemetry.SpaceKeyTag));
        Assert.Equal(2, import.GetTagItem(ImporterTelemetry.PageCountTag));

        var createPages = captured.Single(a => a.OperationName == ImporterTelemetry.CreatePagesSpan);
        Assert.Equal(2, createPages.GetTagItem(ImporterTelemetry.CreatedCountTag));
        Assert.Equal(0, createPages.GetTagItem(ImporterTelemetry.SkippedCountTag));

        var uploads = captured.Single(a => a.OperationName == ImporterTelemetry.UploadAttachmentsSpan);
        Assert.Equal(1, uploads.GetTagItem(ImporterTelemetry.AttachmentCountTag));

        var convert = captured.Single(a => a.OperationName == ImporterTelemetry.ConvertContentSpan);
        Assert.Equal(2, convert.GetTagItem(ImporterTelemetry.PageCountTag));
    }

    [Fact]
    public async Task NoPageTitleOrBodyEverReachesASpan()
    {
        var (listener, captured) = ListenToImporter();
        using (listener)
        {
            var export = new ConfluenceExportSpace("ENG", "Engineering", "Engineering docs",
            [
                Page("1", null, "ZZSENTINELTITLEZZ", "<p>ZZSENTINELBODYZZ</p>",
                [
                    new ConfluenceExportAttachment("a1", "ZZSENTINELFILENAMEZZ.png", "image/png", () => new MemoryStream([1])),
                ]),
            ]);
            Assert.True((await CreateImporter().ImportAsync(export, DefaultOptions())).Success);
        }

        Assert.NotEmpty(captured);
        foreach (var span in captured)
        {
            Assert.DoesNotContain("ZZSENTINEL", span.DisplayName, StringComparison.OrdinalIgnoreCase);
            foreach (var (key, value) in span.Tags)
            {
                Assert.DoesNotContain("ZZSENTINEL", $"{key}={value}", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheImporterSourceIsNamedForTheServiceDefaultsWildcardToFind()
    {
        // Mirrors TelemetryRegistrationTests in RocketWiki.Api.Tests: the CLI wires no
        // exporter of its own, but an in-process host with ServiceDefaults subscribes by
        // the RocketWiki.* pattern, so the name has to match the convention.
        Assert.StartsWith("RocketWiki.", ImporterTelemetry.ActivitySource.Name, StringComparison.Ordinal);
        Assert.Equal(ImporterTelemetry.SourceName, ImporterTelemetry.ActivitySource.Name);
    }

    /// <summary>
    /// The Importer's half of TelemetryNamingTests (RocketWiki.Api.Tests): design.md §15
    /// names tags <c>rocketwiki.&lt;area&gt;.&lt;tag&gt;</c>. Asserted here rather than
    /// there for the same reason as the source-name check above — this project is
    /// deliberately outside the API's reference graph. The Importer declares no meter, so
    /// tag constants are the whole surface.
    /// </summary>
    [Fact]
    public void EveryTagKeyFollowsTheSectionFifteenNamingConvention()
    {
        var pattern = new Regex(@"^rocketwiki\.[a-z0-9_]+\.[a-z0-9_.]+$");
        var violations = new List<string>();
        var tagKeys = new List<string>();

        foreach (var field in typeof(ImporterTelemetry).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field is not { IsLiteral: true, IsInitOnly: false }
                || field.FieldType != typeof(string)
                || !field.Name.EndsWith("Tag", StringComparison.Ordinal))
            {
                continue;
            }

            var key = (string)field.GetRawConstantValue()!;
            tagKeys.Add(key);

            if (!pattern.IsMatch(key))
            {
                violations.Add($"tag ImporterTelemetry.{field.Name} is keyed '{key}'");
            }
        }

        Assert.NotEmpty(tagKeys);
        Assert.True(violations.Count == 0,
            "design.md §15: tags must be keyed 'rocketwiki.<area>.<tag>'. Offenders:\n"
            + string.Join('\n', violations.Select(v => "  - " + v)));
    }
}
