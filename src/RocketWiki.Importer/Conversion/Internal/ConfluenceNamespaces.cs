using System.Xml.Linq;

namespace RocketWiki.Importer.Conversion.Internal;

/// <summary>
/// The two custom XML namespaces Confluence storage format uses for macros (<c>ac:</c>) and
/// resource identifiers (<c>ri:</c>). A page's stored body is an XHTML fragment (no single
/// root, no namespace declarations of its own) — the converter wraps it in a synthetic root
/// element that declares these so the fragment parses as-is.
/// </summary>
internal static class ConfluenceNamespaces
{
    public const string AcUri = "http://www.atlassian.com/schema/confluence/4/ac/";
    public const string RiUri = "http://www.atlassian.com/schema/confluence/4/ri/";

    public static readonly XNamespace Ac = AcUri;
    public static readonly XNamespace Ri = RiUri;
}
