using System.Diagnostics;

namespace RocketWiki.Importer.Telemetry;

/// <summary>
/// Spans for the Confluence import pipeline's three passes (design.md §13, §15). A
/// migration run is a long batch job over an unfamiliar export, and "which pass is it
/// stuck in" is the first question anyone asks; the <c>ImportReport</c> only answers it
/// once the run is over.
///
/// **No exporter is wired up in this project, deliberately.** An <c>ActivitySource</c>
/// with nothing listening costs a null check per span, so the CLI stays a plain console
/// tool with no OpenTelemetry dependency and no OTLP configuration to get wrong on an
/// air-gapped machine. If the importer is ever run in-process by a host that has
/// ServiceDefaults, the <c>RocketWiki.*</c> wildcard picks these up with no change here.
///
/// design.md §15 applies to a CLI exactly as it does to the API: page titles, Confluence
/// bodies, converted Markdown and author names all pass through this pipeline, and none
/// of them appear on a span. Counts and page ids only.
/// </summary>
public static class ImporterTelemetry
{
    public const string SourceName = "RocketWiki.Importer";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public const string ImportSpaceSpan = "rocketwiki.import.space";
    public const string CreatePagesSpan = "rocketwiki.import.create_pages";
    public const string UploadAttachmentsSpan = "rocketwiki.import.upload_attachments";
    public const string ConvertContentSpan = "rocketwiki.import.convert_content";

    public const string PageCountTag = "rocketwiki.import.page_count";
    public const string CreatedCountTag = "rocketwiki.import.created_count";
    public const string AttachmentCountTag = "rocketwiki.import.attachment_count";
    public const string SkippedCountTag = "rocketwiki.import.skipped_count";

    /// <summary>
    /// The space KEY is tagged, not the name or description: a key is the space
    /// identifier §15 permits, while the name is operator-authored prose.
    /// </summary>
    public const string SpaceKeyTag = "rocketwiki.space.key";

    public static Activity? StartSpan(string name) =>
        ActivitySource.StartActivity(name, ActivityKind.Internal);
}
