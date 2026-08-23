namespace RocketWiki.Sync.Cli;

public enum SyncVerb
{
    Export,
    Import,
}

/// <summary>
/// Parsed command line for one RocketWiki.Sync run (design.md §12 Operations: two
/// verbs, export on low, import on high). Same hand-rolled-parser convention as
/// RocketWiki.Importer.Cli.CliOptions - no parser package dependency for a tool this
/// small, and unrecognized flags fail closed to a usage message rather than a guess.
/// </summary>
public sealed class SyncCliOptions
{
    public required SyncVerb Verb { get; init; }
    public required string ConnectionString { get; init; }
    public required string AttachmentsRoot { get; init; }

    // export
    public string? OutputDirectory { get; init; }
    public string? InstanceId { get; init; }
    public string? BaselineSpaceKey { get; init; }

    // import
    public string? BundlePath { get; init; }
    public string? OriginInstanceId { get; init; }
}
