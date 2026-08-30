namespace RocketWiki.Importer.Export;

/// <summary>
/// Ceilings on what a Confluence space export is allowed to decompress to.
///
/// <para>The export is a zip produced by <b>another organisation's</b> Confluence and
/// handed to an operator — the importer has no way to know it is well-formed, and every
/// structural check this reader makes (is there an entities.xml, is there exactly one
/// Space object) happens after something has already been decompressed. So none of them
/// stops a zip bomb: a few hundred kilobytes of compressed zeroes expanding into the
/// importing host's memory, taking a machine on the regulated network with it.</para>
///
/// <para>Two bounds, for the same reason the sync bundle has two: the size a zip entry
/// DECLARES is free to read and is checked first, but it is a number the archive's author
/// chose, so the actual read is counted as well
/// (<see cref="Core.Content.BoundedReadStream"/>).</para>
///
/// <para>Generous on purpose — a real space export of a large wiki has to fit — but
/// bounded. Injectable so a test can prove the refusal with kilobytes.</para>
/// </summary>
public sealed record ConfluenceExportLimits
{
    public static ConfluenceExportLimits Default { get; } = new();

    /// <summary>Entries in the archive: entities.xml plus one per attachment version.</summary>
    public int MaxEntryCount { get; init; } = 200_000;

    /// <summary>
    /// <c>entities.xml</c>, which is parsed into an in-memory XDocument — so the real cost
    /// is several times this number, and this is the entry a bomb would target. 256 MiB of
    /// XML is a very large space; one that genuinely exceeds it wants exporting in parts.
    /// </summary>
    public long MaxEntitiesXmlBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// One attachment binary. Lower than it might look because the import path buffers an
    /// attachment whole to hash it (AttachmentService), so this is a memory ceiling rather
    /// than a disk one. An attachment over it is reported as a failed attachment and the
    /// rest of the import continues — the run is not abandoned over one file.
    /// </summary>
    public long MaxAttachmentBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Everything the archive decompresses to, summed: the per-entry ceilings
    /// alone would still permit <see cref="MaxEntryCount"/> × <see cref="MaxAttachmentBytes"/>.</summary>
    public long MaxTotalUncompressedBytes { get; init; } = 32L * 1024 * 1024 * 1024;
}
