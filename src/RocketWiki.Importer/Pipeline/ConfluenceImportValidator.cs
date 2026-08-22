using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline.Internal;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Validates a Confluence export end-to-end — builds the page tree, resolves every
/// internal link and attachment reference, converts every page body, and previews every
/// comment thread and label — without creating anything and without any dependency on
/// RocketWiki.Core's services or a running RocketWiki instance at all. This is design.md
/// §16's "trial import" done safely: a repeatable inspection of exactly what a real import
/// would produce and lose, runnable against an export file alone.
/// </summary>
/// <remarks>
/// Shares <see cref="Internal.ImportTreePlanner"/> and <see cref="Internal.ForestOrderer"/>
/// with <see cref="ConfluenceSpaceImporter"/> so both agree on page order, comment-thread
/// order, slugs, and what's orphaned — a validator with its own, different notion of the
/// tree would validate nothing. The ids this assigns
/// (<see cref="PageImportOutcome.RocketWikiPageId"/>, comment ids, and — unlike a real
/// import — labels, which aren't actually created here either) are freshly generated
/// locally, purely so <c>page://</c>/<c>attachment://</c> links in the report look like
/// what a real import would produce; they do not, and cannot, match the ids a real run
/// would actually assign. Label creation has no service-level failure mode to preview
/// (a dry run can't know whether a label name will collide with one already in RocketWiki),
/// so every label in the export is reported as "would apply" — the one place a dry run
/// cannot fully predict a real run, and that gap is called out in RUNBOOK.md.
/// </remarks>
public sealed class ConfluenceImportValidator
{
    public ImportValidationResult Validate(ConfluenceExportSpace export)
    {
        var report = new ImportReport();
        var plan = ImportTreePlanner.Plan(export);
        var resolver = new TwoPassPageIdResolver();
        var localPageIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // Pass 1 (pages) and pass 2 (attachments), same as the real importer, so pass 3's
        // conversion resolves every reference exactly as a real run would.
        foreach (var planned in plan.OrderedPages)
        {
            var localId = Guid.CreateVersion7();
            localPageIds[planned.Page.ConfluencePageId] = localId;
            resolver.RegisterPage(export.Key, planned.Page.ConfluencePageId, planned.Page.Title, localId.ToString());
        }

        foreach (var planned in plan.OrderedPages)
        {
            foreach (var attachment in planned.Page.Attachments)
            {
                resolver.RegisterAttachment(
                    export.Key, planned.Page.ConfluencePageId, planned.Page.Title, attachment.FileName, Guid.CreateVersion7().ToString());
            }
        }

        var converter = new ConfluenceStorageConverter(resolver);
        foreach (var planned in plan.OrderedPages)
        {
            var page = planned.Page;
            var pageContext = new ConfluencePageContext(export.Key, page.Title, page.ConfluencePageId);
            var conversion = converter.Convert(page.StorageBodyXhtml, pageContext);
            var commentOutcomes = PreviewComments(export.Key, page, converter);

            report.AddPage(new PageImportOutcome(
                page.ConfluencePageId,
                page.Title,
                localPageIds[page.ConfluencePageId],
                conversion.Report,
                AttachmentFailures: [],
                ImportAuthorFormatting.Format(page.Author),
                SkippedReason: null,
                ProducedEmptyContent: string.IsNullOrWhiteSpace(conversion.Markdown),
                ConvertedMarkdown: conversion.Markdown,
                Comments: commentOutcomes,
                LabelsApplied: page.Labels));
        }

        foreach (var orphan in plan.OrphanedPages)
        {
            report.AddPage(new PageImportOutcome(
                orphan.ConfluencePageId, orphan.Title, null, null, [], ImportAuthorFormatting.Format(orphan.Author),
                "page was never reached while walking the tree from a root page - likely a cycle or a broken parent reference. Would not be imported."));
        }

        var summary = ImportReportSummarizer.Summarize(report, export.Pages.Count);
        return new ImportValidationResult(export.Key, report, summary);
    }

    private static IReadOnlyList<CommentImportOutcome> PreviewComments(
        string spaceKey, ConfluenceExportPage page, ConfluenceStorageConverter converter)
    {
        var (orderedComments, orphanedComments) = ForestOrderer.OrderParentFirst(
            page.Comments, c => c.ConfluenceCommentId, c => c.ParentConfluenceCommentId);

        var outcomes = new List<CommentImportOutcome>(page.Comments.Count);
        foreach (var comment in orderedComments)
        {
            var commentContext = new ConfluencePageContext(spaceKey, page.Title, page.ConfluencePageId);
            var conversion = converter.Convert(comment.BodyXhtml, commentContext);
            outcomes.Add(new CommentImportOutcome(
                comment.ConfluenceCommentId, Guid.CreateVersion7(), conversion.Report, ImportAuthorFormatting.Format(comment.Author), null, conversion.Markdown));
        }

        foreach (var orphan in orphanedComments)
        {
            outcomes.Add(new CommentImportOutcome(
                orphan.ConfluenceCommentId, null, null, ImportAuthorFormatting.Format(orphan.Author),
                "comment was never reached while walking its thread from a root comment - likely a cycle or a broken parent reference. Would not be imported."));
        }

        return outcomes;
    }
}
