using HotChocolate.Types;
using RocketWiki.Core.Access;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Housekeeping for the <c>ProtectiveMarking</c> object type Hot Chocolate discovers
/// through <c>PageEntryView.Marking</c> before <see cref="PageEntryViewTypeExtension"/>
/// rebinds that field to <c>PageMarkingView</c>: the discovered type stays in the schema
/// (no field resolves to it), and convention-based inference would publish
/// <c>ProtectiveMarking.Format(SelectorCatalog)</c> as a field taking the catalog as an
/// argument — dragging an input type for the catalog into the SDL that nothing can build.
/// Bound explicitly instead, so inference never runs over the class and the formatter
/// stays server-side (design.md §21.1: one formatter, one label field,
/// <c>PageMarkingView.label</c>). Every marking a client can actually reach is a
/// <c>PageMarkingView</c>.
/// </summary>
public sealed class ProtectiveMarkingType : ObjectType<ProtectiveMarking>
{
    protected override void Configure(IObjectTypeDescriptor<ProtectiveMarking> descriptor)
    {
        descriptor.BindFieldsExplicitly();
        descriptor.Field(m => m.Level);
        descriptor.Field(m => m.Selectors);
        descriptor.Field(m => m.HasSelectors);
        descriptor.Field(m => m.Prefix);
        descriptor.Field(m => m.HasPrefix);
        descriptor.Field(m => m.EyesOnly);
        descriptor.Field(m => m.HasEyesOnly);
    }
}
