using RocketWiki.Core.Services;
using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Export;
using RocketWiki.Importer.Pipeline.Internal;
using RocketWiki.Importer.Telemetry;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// Orchestrates a full Confluence space import (design.md §13 steps 1–4) against
/// RocketWiki's own <c>RocketWiki.Core.Services</c> interfaces — never against EF Core or
/// <c>RocketWiki.Data</c> directly. Space and page creation, attachment upload, and
/// content-hashing all happen exactly as they would for any other caller of those
/// services; this class only supplies the orchestration (tree order, two-pass id
/// resolution, per-page conversion, report aggregation) those services don't do
/// themselves. <see cref="ConfluenceImportValidator"/> runs the same conversion and tree
/// logic with no side effects at all — run that first (design.md §16).
/// </summary>
/// <remarks>
/// <para>
/// <b>The space-admin bootstrap gap found while building this is now fixed upstream</b>
/// (design.md §6.5.1): a freshly created <c>Space</c> used to have zero <c>AccessRule</c>
/// rows with no way to create the first one through the public service surface at all.
/// <c>ISpaceService.CreateAsync</c> now takes an <see cref="RocketWiki.Core.Services.InitialSpaceGrant"/>
/// and commits it atomically with the space itself, which is also why this importer no
/// longer depends on <c>IAccessRuleService</c> at all — there is nothing left for it to do here.
/// </para>
/// <para>
/// <b>Label re-runs are a known gap.</b> <c>ILabelService</c> has no "find label by name"
/// accessor — only <c>CreateLabelAsync</c> (fails if the name is already taken) and
/// <c>GetPagesByLabelAsync</c> (takes a name, returns pages, not an id). Within one run
/// this doesn't matter (labels are cached by name and created at most once each — see
/// <see cref="ImportLabelsAsync"/>), but re-running an import after a partial failure, or
/// importing into a space where the label already exists, means this importer cannot
/// discover the existing label's id and will report a failure instead of attaching to it.
/// </para>
/// </remarks>
public sealed class ConfluenceSpaceImporter
{
    private const string PlaceholderContent = "_Migrating from Confluence…_";

    private readonly ISpaceService _spaceService;
    private readonly IPageService _pageService;
    private readonly IAttachmentService _attachmentService;
    private readonly ICommentService _commentService;
    private readonly ILabelService _labelService;

    public ConfluenceSpaceImporter(
        ISpaceService spaceService,
        IPageService pageService,
        IAttachmentService attachmentService,
        ICommentService commentService,
        ILabelService labelService)
    {
        _spaceService = spaceService;
        _pageService = pageService;
        _attachmentService = attachmentService;
        _commentService = commentService;
        _labelService = labelService;
    }

    /// <summary>
    /// Imports one space. <b>Never throws for a content problem</b> — every per-page,
    /// per-comment, per-attachment and per-label failure is reported instead, because an
    /// exception out of here is unrecoverable in a way none of them are: each service
    /// commits with its own SaveChanges, so there is no transaction to roll back, and
    /// the report is written by the CALLER after this returns. A throw at page 700 of
    /// 900 therefore left 699 pages and every attachment committed, no report at all,
    /// and a re-run blocked by the space key already existing.
    ///
    /// <para>The outer catch below is the backstop for the failures nobody predicted —
    /// a dropped connection, a service contract that changed underneath this code. It
    /// does not hide them: the exception type and message become the
    /// <see cref="ImportResult.BlockedReason"/>, the caller writes the partial report,
    /// and the CLI exits non-zero. Losing the record of what WAS written is strictly
    /// worse than an ugly stack trace, and until now that is what happened.</para>
    /// </summary>
    public async Task<ImportResult> ImportAsync(ConfluenceExportSpace export, ImportOptions options, CancellationToken cancellationToken = default)
    {
        var report = new ImportReport();
        try
        {
            return await ImportCoreAsync(export, options, report, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ImportResult(
                false, null, report, ImportReportSummarizer.Summarize(report, export.Pages.Count),
                $"the import stopped part-way through with an unexpected {ex.GetType().Name}: {ex.Message}. " +
                "Everything reported below was already written and is still in the database.");
        }
    }

    private async Task<ImportResult> ImportCoreAsync(
        ConfluenceExportSpace export, ImportOptions options, ImportReport report, CancellationToken cancellationToken)
    {
        // design.md §15: one span per pipeline stage, carrying counts and the space key
        // only. Titles, bodies, converted Markdown and author names all flow through the
        // code below and none of them belong in a trace.
        using var importActivity = ImporterTelemetry.StartSpan(ImporterTelemetry.ImportSpaceSpan);
        importActivity?.SetTag(ImporterTelemetry.SpaceKeyTag, export.Key);
        importActivity?.SetTag(ImporterTelemetry.PageCountTag, export.Pages.Count);

        // Recorded BEFORE the space is created, so a run blocked at space creation
        // still reports what the source restricted. design.md §13: reported, never
        // translated — nothing below reads this back to build a rule.
        report.AddSourceSpacePermissions(export.Permissions);

        var spaceResult = await _spaceService.CreateAsync(
            new CreateSpaceRequest(export.Key, export.Name, export.Description),
            options.InitialSpaceGrant, isInstanceAdmin: true, options.ActingUserId, options.AuditContext, cancellationToken);
        if (!spaceResult.IsSuccess)
        {
            var blockedSummary = ImportReportSummarizer.Summarize(report, export.Pages.Count);
            return new ImportResult(false, null, report, blockedSummary, $"Could not create space '{export.Key}': {DescribeError(spaceResult.Error)}");
        }

        var space = spaceResult.Value;

        var plan = ImportTreePlanner.Plan(export);
        var resolver = new TwoPassPageIdResolver();
        var realPageIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var skippedConfluenceIds = new HashSet<string>(StringComparer.Ordinal);

        // --- Pass 1: create every page as a stub, parent before child (ImportTreePlanner
        // guarantees this order), so every Confluence page id maps to a real RocketWiki
        // page id before pass 3 tries to resolve a link to any of them.
        // Each pass span is started and disposed explicitly rather than with a `using`
        // block, to avoid re-indenting these loops purely for a scope. An exception out
        // of a pass leaves its span unstopped, which is acceptable here: it aborts the
        // whole import, so there is no later work for a stale Activity.Current to
        // misparent.
        var pass1 = ImporterTelemetry.StartSpan(ImporterTelemetry.CreatePagesSpan);
        foreach (var planned in plan.OrderedPages)
        {
            var page = planned.Page;

            if (planned.ParentConfluencePageId is { } parentConfluenceId && skippedConfluenceIds.Contains(parentConfluenceId))
            {
                skippedConfluenceIds.Add(page.ConfluencePageId);
                report.AddPage(new PageImportOutcome(
                    page.ConfluencePageId, page.Title, null, null, [], ImportAuthorFormatting.Format(page.Author),
                    "skipped because an ancestor page failed to create", SourceRestrictions: page.Restrictions));
                continue;
            }

            var parentRealId = planned.ParentConfluencePageId is { } pid ? realPageIds[pid] : (Guid?)null;
            var createResult = await _pageService.CreatePageAsync(
                new CreatePageRequest(space.Id, parentRealId, planned.Slug, page.Title, PlaceholderContent),
                options.ImporterPrincipal, options.ActingUserId, options.AuditContext, cancellationToken);

            if (!createResult.IsSuccess)
            {
                skippedConfluenceIds.Add(page.ConfluencePageId);
                report.AddPage(new PageImportOutcome(
                    page.ConfluencePageId, page.Title, null, null, [], ImportAuthorFormatting.Format(page.Author),
                    $"page creation failed: {DescribeError(createResult.Error)}", SourceRestrictions: page.Restrictions));
                continue;
            }

            var realId = createResult.Value.Id;
            realPageIds[page.ConfluencePageId] = realId;
            resolver.RegisterPage(export.Key, page.ConfluencePageId, page.Title, realId.ToString());
        }

        pass1?.SetTag(ImporterTelemetry.CreatedCountTag, realPageIds.Count);
        pass1?.SetTag(ImporterTelemetry.SkippedCountTag, skippedConfluenceIds.Count);
        pass1?.Dispose();

        // --- Pass 2: upload every attachment for every page that was actually created,
        // so pass 3 can resolve attachment:// references to real ids too.
        var pass2 = ImporterTelemetry.StartSpan(ImporterTelemetry.UploadAttachmentsSpan);
        var uploadedAttachmentCount = 0;
        var attachmentFailuresByPage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var planned in plan.OrderedPages)
        {
            var page = planned.Page;
            if (!realPageIds.TryGetValue(page.ConfluencePageId, out var realPageId))
            {
                continue;
            }

            foreach (var attachment in page.Attachments)
            {
                await using var content = attachment.OpenContent();
                var uploadResult = await _attachmentService.UploadAsync(
                    new UploadAttachmentRequest(realPageId, attachment.FileName, attachment.ContentType, content),
                    options.ImporterPrincipal, options.ActingUserId, options.AuditContext, cancellationToken);

                if (!uploadResult.IsSuccess)
                {
                    var failures = attachmentFailuresByPage.TryGetValue(page.ConfluencePageId, out var list)
                        ? list
                        : attachmentFailuresByPage[page.ConfluencePageId] = [];
                    failures.Add($"'{attachment.FileName}': {DescribeError(uploadResult.Error)}");
                    continue;
                }

                resolver.RegisterAttachment(export.Key, page.ConfluencePageId, page.Title, attachment.FileName, uploadResult.Value.Id.ToString());
                uploadedAttachmentCount++;
            }
        }

        pass2?.SetTag(ImporterTelemetry.AttachmentCountTag, uploadedAttachmentCount);
        pass2?.Dispose();

        // --- Pass 3: every page/attachment id is now known, so convert real bodies,
        // replace each page's placeholder content, then import its comments and labels
        // (both need the page's real id, but neither needs to happen before anything
        // else - unlike attachments, nothing in step 3's own body conversion depends on
        // comments or labels existing yet).
        var pass3 = ImporterTelemetry.StartSpan(ImporterTelemetry.ConvertContentSpan);
        var convertedPageCount = 0;
        var converter = new ConfluenceStorageConverter(resolver);
        var labelIdCache = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var planned in plan.OrderedPages)
        {
            var page = planned.Page;
            if (!realPageIds.TryGetValue(page.ConfluencePageId, out var realPageId))
            {
                continue;
            }

            var pageContext = new ConfluencePageContext(export.Key, page.Title, page.ConfluencePageId);
            var attachmentFailures = (IReadOnlyList<string>?)attachmentFailuresByPage.GetValueOrDefault(page.ConfluencePageId) ?? [];

            // A body that will not parse degrades to the raw body in a code block — it
            // does not abort the run and it does not skip the page. Aborting left every
            // earlier page committed with no report and no way to re-run (each service
            // commits with its own SaveChanges; there is no transaction here). Skipping
            // would import a page that looks migrated and is silently empty, and would
            // orphan its children. Keeping the original text loses nothing.
            ConversionResult? conversion = null;
            string? conversionFailure = null;
            string markdown;
            try
            {
                conversion = converter.Convert(page.StorageBodyXhtml, pageContext);
                markdown = conversion.Markdown;
            }
            catch (ConfluenceConversionException ex)
            {
                conversionFailure = ex.Message;
                markdown = UnconvertedBodyFallback.Build(page.StorageBodyXhtml, ex.Message);
            }

            var updateResult = await _pageService.UpdatePageContentAsync(
                new UpdatePageContentRequest(realPageId, ExpectedRevisionNumber: 1, page.Title, markdown,
                    EditSummary: conversionFailure is null
                        ? "Imported from Confluence"
                        : "Imported from Confluence (body could not be converted — original preserved)"),
                options.ImporterPrincipal, options.ActingUserId, options.AuditContext, cancellationToken: cancellationToken);

            var skippedReason = (updateResult.IsSuccess, conversionFailure) switch
            {
                (false, _) => $"page was created but its content could not be saved: {DescribeError(updateResult.Error)} - it still holds placeholder content.",
                (true, not null) => $"page body could not be converted: {conversionFailure} - the page was imported with its original Confluence body preserved verbatim in a code block, and needs converting by hand.",
                _ => null,
            };

            var commentOutcomes = await ImportCommentsAsync(export.Key, page, realPageId, converter, options, cancellationToken);
            var (labelsApplied, labelFailures) = await ImportLabelsAsync(page, realPageId, space.Id, labelIdCache, options, cancellationToken);

            report.AddPage(new PageImportOutcome(
                page.ConfluencePageId, page.Title, realPageId, conversion?.Report, attachmentFailures, ImportAuthorFormatting.Format(page.Author),
                skippedReason,
                // A preserved body is never "empty content": that flag means the converter
                // ran and produced nothing, which is a different thing to investigate.
                ProducedEmptyContent: updateResult.IsSuccess && conversionFailure is null && string.IsNullOrWhiteSpace(markdown),
                ConvertedMarkdown: markdown, Comments: commentOutcomes, LabelsApplied: labelsApplied, LabelFailures: labelFailures,
                SourceRestrictions: page.Restrictions));
            convertedPageCount++;
        }

        pass3?.SetTag(ImporterTelemetry.PageCountTag, convertedPageCount);
        pass3?.Dispose();

        foreach (var orphan in plan.OrphanedPages)
        {
            report.AddPage(new PageImportOutcome(
                orphan.ConfluencePageId, orphan.Title, null, null, [], ImportAuthorFormatting.Format(orphan.Author),
                "page was never reached while walking the tree from a root page - likely a cycle or a broken parent reference. Not imported.",
                SourceRestrictions: orphan.Restrictions));
        }

        var summary = ImportReportSummarizer.Summarize(report, export.Pages.Count);
        return new ImportResult(true, space.Id, report, summary, BlockedReason: null);
    }

    /// <summary>
    /// Imports one page's comment thread in parent-before-child order (design.md §5:
    /// threading matters, and <c>AddCommentAsync</c> validates that a reply's parent
    /// comment already exists on the same page — the same requirement pages have with
    /// their own parent). A comment whose parent failed to create is skipped along with
    /// the rest of that sub-thread, mirroring exactly how a page's failed ancestor skips
    /// its descendants (data-model.md §5: the tombstone model means a comment thread's
    /// shape matters even when part of it can't be created).
    /// </summary>
    private async Task<IReadOnlyList<CommentImportOutcome>> ImportCommentsAsync(
        string spaceKey, ConfluenceExportPage page, Guid realPageId, ConfluenceStorageConverter converter,
        ImportOptions options, CancellationToken cancellationToken)
    {
        var (orderedComments, orphanedComments) = ForestOrderer.OrderParentFirst(
            page.Comments, c => c.ConfluenceCommentId, c => c.ParentConfluenceCommentId);

        var outcomes = new List<CommentImportOutcome>(page.Comments.Count);
        var realCommentIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var skippedCommentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var comment in orderedComments)
        {
            if (comment.ParentConfluenceCommentId is { } parentId && skippedCommentIds.Contains(parentId))
            {
                skippedCommentIds.Add(comment.ConfluenceCommentId);
                outcomes.Add(new CommentImportOutcome(
                    comment.ConfluenceCommentId, null, null, ImportAuthorFormatting.Format(comment.Author),
                    "skipped because its parent comment failed to create"));
                continue;
            }

            var parentRealId = comment.ParentConfluenceCommentId is { } pid ? realCommentIds.GetValueOrDefault(pid) : (Guid?)null;
            var commentContext = new ConfluencePageContext(spaceKey, page.Title, page.ConfluencePageId);

            ConversionResult conversion;
            try
            {
                conversion = converter.Convert(comment.BodyXhtml, commentContext);
            }
            catch (ConfluenceConversionException ex)
            {
                // Skipped exactly like a comment whose creation failed, replies and all:
                // a thread hanging off a comment that does not exist is not a thread.
                skippedCommentIds.Add(comment.ConfluenceCommentId);
                outcomes.Add(new CommentImportOutcome(
                    comment.ConfluenceCommentId, null, null, ImportAuthorFormatting.Format(comment.Author),
                    $"comment body could not be converted: {ex.Message}"));
                continue;
            }

            var addResult = await _commentService.AddCommentAsync(
                new AddCommentRequest(realPageId, parentRealId, conversion.Markdown),
                options.ImporterPrincipal, options.ActingUserId, options.AuditContext, cancellationToken);

            if (!addResult.IsSuccess)
            {
                skippedCommentIds.Add(comment.ConfluenceCommentId);
                outcomes.Add(new CommentImportOutcome(
                    comment.ConfluenceCommentId, null, conversion.Report, ImportAuthorFormatting.Format(comment.Author),
                    $"comment creation failed: {DescribeError(addResult.Error)}", conversion.Markdown));
                continue;
            }

            realCommentIds[comment.ConfluenceCommentId] = addResult.Value.Id;
            outcomes.Add(new CommentImportOutcome(
                comment.ConfluenceCommentId, addResult.Value.Id, conversion.Report, ImportAuthorFormatting.Format(comment.Author), null, conversion.Markdown));
        }

        foreach (var orphan in orphanedComments)
        {
            outcomes.Add(new CommentImportOutcome(
                orphan.ConfluenceCommentId, null, null, ImportAuthorFormatting.Format(orphan.Author),
                "comment was never reached while walking its thread from a root comment - likely a cycle or a broken parent reference. Not imported."));
        }

        return outcomes;
    }

    /// <summary>
    /// Attaches every label on this page, creating each label definition at most once per
    /// import run (<paramref name="labelIdCache"/> is shared across all pages in the
    /// space, since the same label name is expected to recur across many pages). See this
    /// class's remarks for the known gap when a label already exists from a prior run.
    /// </summary>
    private async Task<(IReadOnlyList<string> Applied, IReadOnlyList<string> Failures)> ImportLabelsAsync(
        ConfluenceExportPage page, Guid realPageId, Guid spaceId, Dictionary<string, Guid> labelIdCache,
        ImportOptions options, CancellationToken cancellationToken)
    {
        if (page.Labels.Count == 0)
        {
            return ([], []);
        }

        var applied = new List<string>();
        var failures = new List<string>();

        foreach (var labelName in page.Labels)
        {
            if (!labelIdCache.TryGetValue(labelName, out var labelId))
            {
                var createResult = await _labelService.CreateLabelAsync(
                    new CreateLabelRequest(spaceId, labelName), options.ImporterPrincipal, options.ActingUserId, options.AuditContext, cancellationToken);

                if (!createResult.IsSuccess)
                {
                    failures.Add(
                        $"'{labelName}': could not create ({DescribeError(createResult.Error)}). If this label " +
                        "already exists (e.g. a re-run after a partial failure), it must be attached manually - " +
                        "ILabelService has no way to look up an existing label's id by name.");
                    continue;
                }

                labelId = createResult.Value.Id;
                labelIdCache[labelName] = labelId;
            }

            var attachResult = await _labelService.AttachLabelAsync(
                new AttachLabelRequest(realPageId, labelId), options.ImporterPrincipal, options.ActingUserId, options.AuditContext, cancellationToken);

            if (!attachResult.IsSuccess)
            {
                failures.Add($"'{labelName}': could not attach ({DescribeError(attachResult.Error)})");
                continue;
            }

            applied.Add(labelName);
        }

        return (applied, failures);
    }

    private static string DescribeError(PageMutationError? error) => error switch
    {
        null => "unknown error",
        NotFoundError e => $"not found ({e.Id})",
        ValidationError e => e.Message,
        ForbiddenError e => $"forbidden ({e.Reason})",
        ReadOnlyReplicaError => "space is a read-only replica",
        StaleRevisionError e => $"stale revision (expected {e.ExpectedRevisionNumber}, actual {e.ActualRevisionNumber})",
        SubtreeOperationForbiddenError e => $"{e.BlockedPageCount} page(s) blocked",
        _ => error.GetType().Name,
    };
}
