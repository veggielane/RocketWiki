using RocketWiki.Core.Enums;

namespace RocketWiki.Importer.Cli;

/// <summary>Parsed and validated command-line arguments — see <see cref="ImporterCli"/> for the flag reference.</summary>
internal sealed record CliOptions
{
    public required string ExportPath { get; init; }
    public required string ExpectedSpaceKey { get; init; }
    public required string ReportPath { get; init; }
    public required bool DryRun { get; init; }

    // Real-run only — all required together when DryRun is false.
    public string? ConnectionString { get; init; }
    public string? AttachmentsRoot { get; init; }
    public Guid? ActingUserId { get; init; }
    public string? ImporterPrincipalUserId { get; init; }
    public SpaceRole? InitialGrantRole { get; init; }
    public string? InitialGrantExpressionJson { get; init; }
    public string LocalInstanceId { get; init; } = "standalone";
}
