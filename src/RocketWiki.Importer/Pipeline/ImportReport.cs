using RocketWiki.Importer.Conversion;
using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Pipeline;

/// <summary>
/// What happened to one Confluence comment during import — the same shape as
/// <see cref="PageImportOutcome"/>, minus attachments and labels (comments don't carry
/// either). <see cref="RocketWikiCommentId"/> is null iff the comment (or, for a reply,
/// its parent) failed to create.
/// </summary>
public sealed record CommentImportOutcome(
    string ConfluenceCommentId,
    Guid? RocketWikiCommentId,
    ConversionReport? ConversionReport,
    string? OriginalAuthor,
    string? SkippedReason,
    string? ConvertedMarkdown = null);

/// <summary>
/// What happened to one Confluence page during import. <see cref="RocketWikiPageId"/> is
/// null iff the page was never created (either it failed outright, or an ancestor did and
/// it was skipped along with the rest of that subtree — <see cref="SkippedReason"/> says
/// which). <see cref="ConversionReport"/> is the same per-page report
/// <see cref="ConfluenceStorageConverter"/> always produces (design.md §13 step 4) — null
/// only when the page's body was never converted at all. <see cref="ConvertedMarkdown"/> is
/// carried through by both a dry run (nothing else shows what the page would look like)
/// and a real import (useful when <see cref="SkippedReason"/> says the save itself failed,
/// to see what would have been saved).
/// </summary>
public sealed record PageImportOutcome(
    string ConfluencePageId,
    string Title,
    Guid? RocketWikiPageId,
    ConversionReport? ConversionReport,
    IReadOnlyList<string> AttachmentFailures,
    string? OriginalAuthor,
    string? SkippedReason,
    bool ProducedEmptyContent = false,
    string? ConvertedMarkdown = null,
    IReadOnlyList<CommentImportOutcome>? Comments = null,
    IReadOnlyList<string>? LabelsApplied = null,
    IReadOnlyList<string>? LabelFailures = null,
    IReadOnlyList<ConfluenceExportPermission>? SourceRestrictions = null)
{
    /// <summary>This page's comment thread, in parent-before-child order — empty if the page had none, or wasn't itself created.</summary>
    public IReadOnlyList<CommentImportOutcome> Comments { get; init; } = Comments ?? [];

    /// <summary>Label names successfully attached to this page.</summary>
    public IReadOnlyList<string> LabelsApplied { get; init; } = LabelsApplied ?? [];

    /// <summary>Label names that could not be created or attached, with why — e.g. the name already exists and this importer has no way to look up an existing label's id (see ConfluenceSpaceImporter's remarks).</summary>
    public IReadOnlyList<string> LabelFailures { get; init; } = LabelFailures ?? [];

    /// <summary>
    /// Confluence page restrictions the export carried for this page, verbatim and
    /// <b>not applied</b> (design.md §13). The imported page is governed solely by the
    /// space grant the operator supplied, so a non-empty list here means this page was
    /// more restricted in Confluence than it is now — the one thing an admin must act on
    /// before the space is opened to its users.
    /// </summary>
    public IReadOnlyList<ConfluenceExportPermission> SourceRestrictions { get; init; } = SourceRestrictions ?? [];
}

/// <summary>
/// The aggregated, whole-space import report: every page's outcome plus pipeline-level
/// notes that aren't about any one page (the space-grant bootstrap issue, orphaned pages).
/// This is what a content owner reviews after an import — see design.md §13 step 4.
/// </summary>
public sealed class ImportReport
{
    private readonly List<PageImportOutcome> _pages = [];
    private readonly List<string> _pipelineNotes = [];
    private readonly List<ConfluenceExportPermission> _sourceSpacePermissions = [];

    public IReadOnlyList<PageImportOutcome> Pages => _pages;

    public IReadOnlyList<string> PipelineNotes => _pipelineNotes;

    /// <summary>
    /// Space-level Confluence permissions the export carried, verbatim and <b>not
    /// applied</b> — design.md §13's "reported, never translated". The imported space is
    /// governed solely by the operator's <c>--grant-expression</c>, so this list is the
    /// difference between what the source allowed and what the destination now allows,
    /// and re-applying it is a deliberate admin decision.
    /// </summary>
    public IReadOnlyList<ConfluenceExportPermission> SourceSpacePermissions => _sourceSpacePermissions;

    /// <summary>Every page restriction the export carried, paired with the page it was on ("" = the export gave no owning page).</summary>
    public IEnumerable<(string PageTitle, ConfluenceExportPermission Restriction)> SourcePageRestrictions =>
        _pages.SelectMany(p => p.SourceRestrictions.Select(r => (p.Title, r)));

    /// <summary>
    /// True if any page or comment has a lossy conversion issue, failed outright, or had
    /// an attachment/label failure — the "does a human need to look at this" signal.
    ///
    /// <para>A permission found in the source counts. It is not a failure and nothing
    /// went wrong, but an import that silently drops a five-person page restriction into
    /// a space anyone with the grant can read is precisely the over-open outcome §13
    /// names as the worse of the two directions, and "no news" would report it as
    /// clean.</para>
    /// </summary>
    public bool NeedsReview =>
        _pipelineNotes.Count > 0
        || _sourceSpacePermissions.Count > 0
        || _pages.Any(p => p.SkippedReason is not null
            || p.AttachmentFailures.Count > 0
            || p.LabelFailures.Count > 0
            || p.SourceRestrictions.Count > 0
            || p.ProducedEmptyContent
            || p.ConversionReport?.HasLossyIssues == true
            || p.Comments.Any(c => c.SkippedReason is not null || c.ConversionReport?.HasLossyIssues == true));

    public void AddPage(PageImportOutcome outcome) => _pages.Add(outcome);

    public void AddNote(string note) => _pipelineNotes.Add(note);

    public void AddSourceSpacePermissions(IEnumerable<ConfluenceExportPermission> permissions) =>
        _sourceSpacePermissions.AddRange(permissions);
}

/// <summary>
/// The outcome of importing one space. <see cref="BlockedReason"/> is set only when the
/// import could not proceed at all (space creation itself failed) — a space that was
/// created but had some pages fail is still <see cref="Success"/>, with those failures
/// visible in <see cref="Report"/>. Partial success is the expected common case for a
/// real migration, not an error condition (design.md §13: "budget real time for" fidelity
/// issues).
/// </summary>
public sealed record ImportResult(bool Success, Guid? SpaceId, ImportReport Report, ImportValidationSummary Summary, string? BlockedReason);
