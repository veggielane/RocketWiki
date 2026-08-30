namespace RocketWiki.Core.Sync;

/// <summary>
/// Ceilings on what a bundle is allowed to decompress to, applied by
/// <c>BundleImportService</c> before any of it is trusted.
///
/// <para>Why this exists: a bundle arrives across the one-way guarded boundary from a
/// medium the high side treats as untrusted, and it is a <b>zip</b>. Every check the
/// format already has — the manifest hash chain, the payload hash, the per-space
/// sequence — happens <i>after</i> something has been decompressed, so none of them can
/// stop a zip bomb: a few hundred kilobytes of compressed zeroes that expand to tens of
/// gigabytes and take the importing host's memory with them. The importer is the
/// regulated system's own process, so that is an availability hit an attacker gets
/// before authenticating anything at all.</para>
///
/// <para>Two things follow from that and are both done:</para>
/// <list type="number">
/// <item><see cref="System.IO.Compression.ZipArchiveEntry.Length"/> is the size the zip's
/// central directory <i>claims</i> — free to read and worth checking first, but a number
/// the bundle's author chose. It is a fast refusal, never a proof.</item>
/// <item>The copy loop counts the bytes it actually decompresses and stops the moment it
/// passes the ceiling, so a lying central directory buys nothing.</item>
/// </list>
///
/// <para>The defaults are deliberately generous — a real baseline of a large space with
/// its full revision history and every attachment must fit — because the purpose here is
/// to bound the pathological case, not to second-guess a legitimate export. An operator
/// whose genuine bundles exceed one of these has a real conversation to have about
/// splitting the export; a silent OOM is not that conversation.</para>
///
/// <para>Injectable so tests can drive the refusal with a few kilobytes instead of a few
/// gigabytes; production always uses <see cref="Default"/>.</para>
/// </summary>
public sealed record BundleLimits
{
    public static BundleLimits Default { get; } = new();

    /// <summary>Entries in the archive. A baseline is manifest + events + one blob per
    /// distinct attachment content, so this is really "how many attachments may one
    /// bundle carry"; it also stops an entry-count bomb (millions of empty entries).</summary>
    public int MaxEntryCount { get; init; } = 100_000;

    /// <summary>manifest.json, read whole into memory. It carries one
    /// <c>SpaceEventRange</c> per space and nothing else that scales with content.</summary>
    public long MaxManifestBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// The NDJSON events entry, read whole into memory. This is the entry that actually
    /// scales with content — a baseline carries every page's current text plus every
    /// revision — and it is also the most expensive one to hold: the bytes are decoded to
    /// a string and then split into lines, so peak cost is several times this number. 256
    /// MiB of JSON is an enormous space; a real one that exceeds it wants splitting.
    /// </summary>
    public long MaxEventsBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>One <c>blobs/*</c> entry. Streamed to storage and never buffered, so this
    /// bounds disk rather than memory; it sits above the API's own 100 MiB attachment cap
    /// (<c>Attachments:MaxSizeBytes</c>) so raising that does not immediately make
    /// legitimate bundles unimportable.</summary>
    public long MaxBlobBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Everything the archive decompresses to, summed. The per-entry ceilings
    /// alone would still permit <see cref="MaxEntryCount"/> × <see cref="MaxBlobBytes"/>,
    /// which is the compression-ratio attack in a different shape.</summary>
    public long MaxTotalUncompressedBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>Lines in the events entry. <see cref="MaxEventsBytes"/> already bounds
    /// this loosely, but each line becomes a parsed object held in a list for the whole
    /// import, so the object count deserves its own ceiling rather than being inferred
    /// from a byte count and the shortest legal line.</summary>
    public int MaxEventLines { get; init; } = 5_000_000;
}
