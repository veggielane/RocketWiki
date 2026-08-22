namespace RocketWiki.Importer.Conversion;

/// <summary>
/// How much a conversion issue actually cost the content owner.
/// </summary>
public enum IssueSeverity
{
    /// <summary>Something changed or was removed, but nothing a reader would consider content was lost
    /// (e.g. a dropped table-of-contents macro, or a passthrough note).</summary>
    Info,

    /// <summary>Content, formatting, or structure was lost or degraded and cannot be recovered from the
    /// converted Markdown alone. A content owner should review the page.</summary>
    Lossy,
}

/// <summary>
/// What kind of thing the issue is about.
/// </summary>
public enum IssueCategory
{
    /// <summary>A <c>&lt;ac:structured-macro&gt;</c> this converter has no specific handling for.</summary>
    UnsupportedMacro,

    /// <summary>An XHTML element that was removed (or unwrapped) because it has no Markdown/v1 equivalent.</summary>
    DroppedElement,

    /// <summary>Content was kept but its fidelity was reduced (merged table cells, alignment, underline,
    /// panel titles flattened into body text, etc.).</summary>
    LossyTransform,

    /// <summary>An <c>&lt;ac:link&gt;</c> or <c>&lt;ri:attachment&gt;</c> reference could not be resolved
    /// to a RocketWiki id via <see cref="IPageIdResolver"/>.</summary>
    UnresolvedLink,
}

/// <summary>
/// One entry in a page's <see cref="ConversionReport"/>: a single unsupported macro, dropped
/// element, or lossy transform, with enough detail for a content owner to find and judge it.
/// </summary>
/// <param name="Severity">How much this cost the reader.</param>
/// <param name="Category">What kind of issue this is.</param>
/// <param name="Message">A human-readable explanation, written for a content owner, not a developer.</param>
/// <param name="Location">A heading-path breadcrumb (e.g. "Setup &gt; Prerequisites") locating the issue
/// within the page, or <see langword="null"/> if it occurred before the first heading.</param>
/// <param name="Detail">Optional extra context — a source snippet, macro name, or URL — for triage.</param>
public sealed record ConversionIssue(
    IssueSeverity Severity,
    IssueCategory Category,
    string Message,
    string? Location = null,
    string? Detail = null);
