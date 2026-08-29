using HotChocolate.Types;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.GraphQL;

/// <summary>
/// Replaces the entry's raw <c>ProtectiveMarking</c> field with the same
/// <see cref="PageMarkingView"/> every other marking in this schema exposes.
///
/// <para>Not cosmetic. <c>ProtectiveMarking</c> carries <c>format</c> (the whole marking
/// string, caveat included) and the level enum, but not <c>levelName</c> — the level
/// ALONE. A badge wants the level; a banner wants the whole thing; and §21.1 is explicit
/// that the two must stay separately named, because a one-character swap between them
/// silently drops a caveat. Handing the client only `format` would push that distinction
/// into the SPA, where the marking components would each have to re-derive it.</para>
///
/// <para>A type extension rather than a change to Core's <see cref="PageEntryView"/>:
/// that record is the service contract, and its marking is a domain value, not a
/// presentation shape. Nothing here reads a database — the marking travelled out of the
/// service with the entry, which is the same "carried, not re-loaded" rule
/// <c>PageTreeNode</c> follows, and for the same reason: a second lookup could return a
/// different row than the one the gate consulted.</para>
/// </summary>
public sealed class PageEntryViewTypeExtension : ObjectTypeExtension<PageEntryView>
{
    protected override void Configure(IObjectTypeDescriptor<PageEntryView> descriptor)
    {
        // Rebound in place rather than ignored-and-re-added: ignoring the property
        // removes the name, and a field added back under it does not reappear.
        descriptor.Field(e => e.Marking)
            .Type<NonNullType<ObjectType<PageMarkingView>>>()
            .Resolve(context => PageMarkingView.From(context.Parent<PageEntryView>().Marking));
    }
}
