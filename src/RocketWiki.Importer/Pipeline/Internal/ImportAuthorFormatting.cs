using RocketWiki.Importer.Export;

namespace RocketWiki.Importer.Pipeline.Internal;

internal static class ImportAuthorFormatting
{
    /// <summary>Shared by the real importer and the dry-run validator, so a page's reported original author reads identically either way.</summary>
    public static string? Format(ConfluenceExportAuthor? author) => author switch
    {
        null => null,
        { DisplayName: { } name, Email: { } email } => $"{name} <{email}>",
        { DisplayName: { } name } => name,
        { Email: { } email } => email,
        _ => null,
    };
}
