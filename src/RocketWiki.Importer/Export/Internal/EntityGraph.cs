using System.Xml.Linq;

namespace RocketWiki.Importer.Export.Internal;

internal abstract record EntityProperty;

internal sealed record ScalarProperty(string Value) : EntityProperty;

internal sealed record ReferenceProperty(string RefId) : EntityProperty;

internal sealed record CollectionProperty(IReadOnlyList<string> RefIds) : EntityProperty;

internal sealed class EntityObject
{
    public required string Id { get; init; }
    public required string Class { get; init; }
    public Dictionary<string, EntityProperty> Properties { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A generic reader for Confluence's <c>entities.xml</c> object-graph format: a flat list
/// of <c>&lt;object class="..."&gt;</c> elements, each with an <c>&lt;id&gt;</c> and a set
/// of <c>&lt;property name="..."&gt;</c> children that are either scalar text, a reference
/// to another object's id, or a collection of such references.
/// </summary>
/// <remarks>
/// This reads the generic shape only — it makes no assumption about which classes or
/// property names exist, deliberately, because that lets it survive small naming
/// differences across Confluence versions without code changes; only the specific
/// property lookups in <see cref="ConfluenceXmlExportReader"/> need to match a real
/// export. This class has not been run against a real Confluence export (see that
/// reader's remarks); it is built from the documented shape of the format.
/// </remarks>
internal sealed class EntityGraph
{
    private readonly Dictionary<string, EntityObject> _byId;

    private EntityGraph(Dictionary<string, EntityObject> byId)
    {
        _byId = byId;
    }

    /// <summary>
    /// Loads the whole <c>entities.xml</c> into an XDocument. <b>A real full-space
    /// export is hundreds of MB</b>, so this is a memory ceiling on how large a space
    /// this tool can import — known, and accepted for now: the graph is queried by id
    /// from every direction (a page’s body, its attachments, a comment’s owner), so a
    /// streaming reader would have to build most of the same index anyway. If an
    /// import dies on memory, this is the line to look at first, and the fix is a
    /// two-pass XmlReader that indexes offsets rather than elements.
    /// </summary>
    public static EntityGraph Parse(Stream entitiesXml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(entitiesXml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ConfluenceExportFormatException($"entities.xml is not well-formed XML: {ex.Message}");
        }

        var root = doc.Root ?? throw new ConfluenceExportFormatException("entities.xml has no root element.");

        var byId = new Dictionary<string, EntityObject>(StringComparer.Ordinal);
        foreach (var objectElement in root.Elements("object"))
        {
            var className = (string?)objectElement.Attribute("class");
            var idElement = objectElement.Element("id");
            if (string.IsNullOrEmpty(className) || idElement is null)
            {
                // An <object> with no class or no id can never be meaningfully referenced
                // by anything else in the graph - skip rather than fail the whole export
                // over one malformed entry.
                continue;
            }

            var entity = new EntityObject { Id = idElement.Value.Trim(), Class = className };
            foreach (var propertyElement in objectElement.Elements("property"))
            {
                var name = (string?)propertyElement.Attribute("name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                entity.Properties[name] = ParseProperty(propertyElement);
            }

            byId[entity.Id] = entity;
        }

        return new EntityGraph(byId);
    }

    public IEnumerable<EntityObject> ObjectsOfClass(string className) =>
        _byId.Values.Where(o => string.Equals(o.Class, className, StringComparison.Ordinal));

    public EntityObject? ById(string id) => _byId.GetValueOrDefault(id);

    public string? GetScalar(EntityObject entity, string propertyName) =>
        entity.Properties.TryGetValue(propertyName, out var property) && property is ScalarProperty scalar
            ? scalar.Value
            : null;

    public string? GetReferenceId(EntityObject entity, string propertyName) =>
        entity.Properties.TryGetValue(propertyName, out var property) && property is ReferenceProperty reference
            ? reference.RefId
            : null;

    public IReadOnlyList<string> GetCollectionIds(EntityObject entity, string propertyName) =>
        entity.Properties.TryGetValue(propertyName, out var property) && property is CollectionProperty collection
            ? collection.RefIds
            : [];

    private static EntityProperty ParseProperty(XElement propertyElement)
    {
        var idChild = propertyElement.Element("id");
        if (idChild is not null)
        {
            return new ReferenceProperty(idChild.Value.Trim());
        }

        var collectionChild = propertyElement.Element("collection");
        if (collectionChild is not null)
        {
            var refIds = collectionChild.Elements("element")
                .Select(e => e.Element("id")?.Value.Trim())
                .Where(id => id is not null)
                .Select(id => id!)
                .ToList();
            return new CollectionProperty(refIds);
        }

        return new ScalarProperty(propertyElement.Value);
    }
}
