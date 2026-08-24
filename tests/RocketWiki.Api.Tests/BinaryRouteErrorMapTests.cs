using System.Reflection;
using Microsoft.AspNetCore.Http;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Http;
using RocketWiki.Core.Services;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// The one domain-error→HTTP-status map the three binary routes share
/// (<see cref="BinaryRoutes.ErrorStatusCode"/>). It used to be three drifted copies:
/// the attachment route mapped <c>ReadOnlyReplica</c> to 403 but <c>NameTaken</c> to
/// 400, the emoji route the reverse, and the avatar route neither — so the same domain
/// error answered with a different status depending on which route produced it.
///
/// Pinned here rather than through the routes because most kinds are structurally
/// unreachable per route (an avatar upload has no space to be a replica of, an
/// attachment has no name to be taken), and a test that can only assert what a route
/// can actually reach would leave exactly the drift that happened unguarded. The
/// endpoint tests cover the reachable ones; this covers the map.
/// </summary>
public class BinaryRouteErrorMapTests
{
    /// <summary>
    /// Every kind <see cref="PageMutationErrorView"/> can produce, with the status the
    /// binary routes answer. Changing a row here is a deliberate API change — the SPA
    /// switches on both the status and the <c>kind</c> discriminator.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> ExpectedStatuses = new Dictionary<string, int>
    {
        ["Forbidden"] = StatusCodes.Status403Forbidden,
        ["SubtreeOperationForbidden"] = StatusCodes.Status403Forbidden,
        ["ReadOnlyReplica"] = StatusCodes.Status403Forbidden,
        ["NotFound"] = StatusCodes.Status404NotFound,
        ["NameTaken"] = StatusCodes.Status409Conflict,
        ["StaleRevision"] = StatusCodes.Status409Conflict,
        ["Validation"] = StatusCodes.Status400BadRequest,
    };

    public static TheoryData<string, int> ExpectedStatusRows()
    {
        var rows = new TheoryData<string, int>();
        foreach (var (kind, status) in ExpectedStatuses)
        {
            rows.Add(kind, status);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(ExpectedStatusRows))]
    public void EachKindMapsToItsDeclaredStatus(string kind, int expectedStatus) =>
        Assert.Equal(expectedStatus, BinaryRoutes.ErrorStatusCode(kind));

    /// <summary>
    /// Totality: every kind the view can emit has an explicit row above (and therefore an
    /// explicit case in the map). A new <see cref="PageMutationError"/> subtype that
    /// reaches a binary route would otherwise silently answer the default 400 — the
    /// failure mode this whole consolidation exists to prevent.
    /// </summary>
    [Fact]
    public void EveryKindTheErrorViewCanProduceIsMappedExplicitly()
    {
        var kinds = ErrorSamples()
            .Select(error => PageMutationErrorView.From(error).Kind)
            .ToHashSet(StringComparer.Ordinal);

        // Non-vacuous: the sweep found real error types, not an empty reflection result.
        Assert.NotEmpty(kinds);

        var unmapped = kinds.Except(ExpectedStatuses.Keys).ToList();
        Assert.True(unmapped.Count == 0,
            "PageMutationErrorView produces kinds the binary-route status map does not name explicitly: "
            + string.Join(", ", unmapped)
            + ". Add a case to BinaryRoutes.ErrorStatusCode and a row to ExpectedStatuses.");

        var stale = ExpectedStatuses.Keys.Except(kinds).ToList();
        Assert.True(stale.Count == 0,
            "The status map names kinds PageMutationErrorView can no longer produce: " + string.Join(", ", stale));
    }

    [Fact]
    public void AnUnknownKindIsABadRequest_NotAServerError() =>
        // The default exists so an unmapped kind degrades to 400 rather than throwing
        // inside a route handler. The test above is what keeps it from being load-bearing.
        Assert.Equal(StatusCodes.Status400BadRequest, BinaryRoutes.ErrorStatusCode("SomethingAddedLater"));

    /// <summary>
    /// One instance of every <see cref="PageMutationError"/> subtype the view supports.
    /// The bundle/sync errors (<c>BundleGapError</c> and friends) are deliberately absent:
    /// <see cref="PageMutationErrorView.From"/> throws for them, which is its own guard —
    /// they belong to the sync CLI's result path and no HTTP route can return one.
    /// </summary>
    private static IEnumerable<PageMutationError> ErrorSamples()
    {
        var subtypes = typeof(PageMutationError).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(PageMutationError).IsAssignableFrom(t));

        foreach (var subtype in subtypes)
        {
            var sample = (PageMutationError)Construct(subtype);

            PageMutationErrorView view;
            try
            {
                view = PageMutationErrorView.From(sample);
            }
            catch (NotSupportedException)
            {
                continue; // Not part of the HTTP-facing vocabulary; the view itself says so.
            }

            Assert.NotNull(view);
            yield return sample;
        }
    }

    private static object Construct(Type type)
    {
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .First();

        var arguments = constructor.GetParameters()
            .Select(p => p.ParameterType == typeof(string) ? "sample" : Activator.CreateInstance(p.ParameterType))
            .ToArray();

        return constructor.Invoke(arguments);
    }
}
