using System.Globalization;

namespace RocketWiki.Data.Services;

/// <summary>
/// Builds the object keys blobs are stored under (design.md §10). One place, because the
/// three call sites were three copies of the same interpolation and one property of them
/// is not obvious enough to re-derive correctly each time.
///
/// <para><b>The date is formatted with the invariant culture, deliberately.</b>
/// <c>$"{date:yyyy/MM}"</c> uses the CURRENT culture, and <c>yyyy</c> is the year in that
/// culture's calendar — under <c>ar-SA</c> (Umm al-Qura) or <c>th-TH</c> (Buddhist) the
/// same instant yields a different year, so the same server on a different locale writes
/// keys into a different prefix. Nothing breaks loudly: old objects stay exactly where
/// they are and stay readable, because the key is stored on the row. What changes is that
/// the prefix stops meaning "written in this Gregorian month", which is the only thing
/// the date segment is for — it exists to keep a bucket listing navigable and to let a
/// retention sweep work by prefix. A janitor scanning <c>attachments/2026/08</c> would
/// quietly skip whatever landed under <c>attachments/1447/02</c>.</para>
/// </summary>
internal static class StorageKeys
{
    public static string ForAttachment(DateTime utcNow) => $"attachments/{Prefix(utcNow)}/{Guid.CreateVersion7()}";

    public static string ForAvatar(DateTime utcNow) => $"avatars/{Prefix(utcNow)}/{Guid.CreateVersion7()}";

    private static string Prefix(DateTime utcNow) => utcNow.ToString("yyyy'/'MM", CultureInfo.InvariantCulture);
}
