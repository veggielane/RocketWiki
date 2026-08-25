using RocketWiki.Core.Access;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.Markings;

public static class PageMarkingViewExtensions
{
    /// <summary>
    /// The exact inverse of <see cref="PageMarkingView.From"/>, for the caller that holds
    /// a view and needs the value object back: <c>PageTreeNode.Marking</c> is a view (it
    /// is carried out of the tree walk for display, §21.9), and an aggregate label over a
    /// tree has to fold real markings.
    ///
    /// <para>Lossless: <c>Level</c>, <c>EyesOnly</c> and <c>Prefix</c> on a view are
    /// already canonical, so <c>Create</c> normalizes nothing. Re-deriving beats
    /// re-querying — a second lookup could read a different row than the walk gated on,
    /// which is the exact reason the tree carries its marking out in the first place.</para>
    ///
    /// <para><b>An extension method, not a member of the record</b>, because
    /// <c>PageMarkingView</c> is a GraphQL object type: a public instance method on it
    /// becomes a published field, and one returning a <c>ProtectiveMarking</c> would drag
    /// a second, competing representation of a marking into the SDL.</para>
    ///
    /// <para><b>Not a route back into enforcement.</b> Nothing may take a view — a display
    /// shape that has been over the wire — and turn it into a marking to gate with. This
    /// exists to render, and its only callers build display labels.</para>
    /// </summary>
    public static ProtectiveMarking ToMarking(this PageMarkingView view) =>
        ProtectiveMarking.Create(view.Level, view.EyesOnly, view.Prefix);
}
