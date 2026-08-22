using System.Xml.Linq;

namespace RocketWiki.Importer.Conversion.Internal;

internal static class XmlNodeExtensions
{
    /// <summary>
    /// Compares an element's local name, ignoring its namespace and case — Confluence storage
    /// format elements (<c>&lt;p&gt;</c>, <c>&lt;td&gt;</c>, ...) sit in the empty namespace,
    /// but comparing by local name keeps this robust if an export ever qualifies them.
    /// </summary>
    public static bool HasLocalName(this XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);
}
