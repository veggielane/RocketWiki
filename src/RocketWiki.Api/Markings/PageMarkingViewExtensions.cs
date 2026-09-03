using RocketWiki.Core.Access;
using RocketWiki.Core.Services;

namespace RocketWiki.Api.Markings;

public static class PageMarkingViewExtensions
{
    /// <summary>
    /// The inverse of <see cref="PageMarkingView.From"/>, for the caller that holds a
    /// view and needs the value object back: <c>PageTreeNode.Marking</c> is a view (it is
    /// carried out of the tree walk for display, §21.9), and an aggregate label over a
    /// tree has to fold real markings.
    ///
    /// <para>Lossless for everything the product can write: <c>Level</c>, <c>EyesOnly</c>
    /// and <c>Selectors</c> on a view are already canonical, so <c>Create</c> normalizes
    /// nothing, and the prefix toggle maps back to <c>UK</c> or none. The one thing it
    /// cannot carry is a legacy prefix that is neither (§21.12's toggle has no third
    /// state), which the view's <c>Label</c> still shows verbatim; the aggregate over a
    /// tree would render such a source as prefixless, which is the same answer
    /// disagreement gets. Re-deriving beats re-querying — a second lookup could read a
    /// different row than the walk gated on, which is the exact reason the tree carries
    /// its marking out in the first place.</para>
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
        ProtectiveMarking.Create(
            view.Level, view.EyesOnly, view.Selectors, view.UkPrefix ? ProtectiveMarking.UkPrefix : null);
}
