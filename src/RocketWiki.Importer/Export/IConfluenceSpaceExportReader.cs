namespace RocketWiki.Importer.Export;

/// <summary>
/// Reads a Confluence space export into the normalized <see cref="ConfluenceExportSpace"/>
/// shape the import pipeline consumes, decoupling "what a Confluence export looks like"
/// from "what the pipeline does with one" — a REST-API-backed reader (design.md §13 point 1
/// mentions this as an alternative to a file export) can implement this same interface
/// without the pipeline changing at all.
/// </summary>
public interface IConfluenceSpaceExportReader
{
    /// <summary>
    /// Reads the export. Takes ownership of <paramref name="exportZip"/> — the returned
    /// <see cref="ConfluenceSpaceExport"/> must be disposed (which disposes the stream)
    /// once the pipeline has finished reading every attachment's content, since attachment
    /// bytes are streamed lazily from the archive rather than buffered eagerly in memory.
    /// </summary>
    ConfluenceSpaceExport Read(Stream exportZip);
}
