namespace RocketWiki.Importer.Pipeline;

/// <summary>The result of validating an export with <see cref="ConfluenceImportValidator"/> — nothing was written anywhere; this is a prediction of what a real import would do.</summary>
public sealed record ImportValidationResult(string SpaceKey, ImportReport Report, ImportValidationSummary Summary);
