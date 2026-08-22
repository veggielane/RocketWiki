using System.IO.Compression;

namespace RocketWiki.Importer.Export;

/// <summary>
/// Owns the open <see cref="ZipArchive"/> backing a <see cref="ConfluenceExportSpace"/>'s
/// lazy attachment streams. Disposing this closes the archive (and, since the reader does
/// not pass <c>leaveOpen: true</c>, the underlying stream too) — do this only after every
/// attachment's <c>OpenContent</c> has been called for the last time.
/// </summary>
public sealed class ConfluenceSpaceExport : IDisposable
{
    private readonly ZipArchive _archive;

    public ConfluenceExportSpace Space { get; }

    internal ConfluenceSpaceExport(ZipArchive archive, ConfluenceExportSpace space)
    {
        _archive = archive;
        Space = space;
    }

    public void Dispose() => _archive.Dispose();
}
