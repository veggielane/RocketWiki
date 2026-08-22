namespace RocketWiki.Importer.Conversion;

/// <summary>
/// The per-page conversion report (design.md §13 step 4): a first-class record of every
/// unsupported macro, dropped element, or lossy transform encountered while converting one
/// page, so a content owner can review exactly what changed instead of discovering it later.
/// </summary>
public sealed class ConversionReport
{
    private readonly List<ConversionIssue> _issues = new();

    /// <summary>All issues recorded during conversion, in document order.</summary>
    public IReadOnlyList<ConversionIssue> Issues => _issues;

    /// <summary>True if any issue actually lost or degraded content (as opposed to purely informational).</summary>
    public bool HasLossyIssues => _issues.Any(i => i.Severity == IssueSeverity.Lossy);

    /// <summary>Records an issue. Never throws away information — every call is retained.</summary>
    public void Add(ConversionIssue issue) => _issues.Add(issue);
}
