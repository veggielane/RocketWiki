using System.Reflection;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// Tripwire for design.md §10's named invariant: "No presigned URLs. Downloads
/// always stream through the API: a presigned URL would bypass both page
/// restrictions (`canView`) and the audit log." The prose ban lives on
/// IFileStorage, S3FileStorage, and the attachment routes; this test is the
/// build-time teeth — a member that mints a URL/URI/presigned link for a storage
/// object cannot be added to the storage surface without failing here, and the
/// interface itself is pinned to exactly its four streaming members so a bypass
/// can't ride in under an innocent-sounding name either.
/// </summary>
public sealed class NoPresignedUrlTripwireTests
{
    /// <summary>The interface AND every implementation: an implementation-only
    /// public method (not on IFileStorage) would still be reachable by anyone
    /// holding the concrete type, so the sweep must not stop at the contract.</summary>
    private static readonly Type[] StorageSurface =
    [
        typeof(IFileStorage),
        typeof(FileSystemFileStorage),
        typeof(S3FileStorage),
        typeof(SqlServerFileStorage),
    ];

    private static readonly string[] ForbiddenNameFragments = ["url", "uri", "presign", "signedlink"];

    [Fact]
    public void NoStorageMember_MintsUrlsOrPresignedLinks()
    {
        var problems = new List<string>();
        var sweptMembers = 0;

        foreach (var type in StorageSurface)
        {
            const BindingFlags visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            foreach (var member in type.GetMembers(visible))
            {
                sweptMembers++;
                foreach (var fragment in ForbiddenNameFragments)
                {
                    if (member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{type.Name}.{member.Name}: name contains '{fragment}'.");
                    }
                }
            }
        }

        // Non-vacuous: the interface alone contributes four methods; an empty sweep
        // means the reflection flags broke, not that the surface went quiet.
        Assert.True(sweptMembers > 0, "Reflection sweep of the storage surface found no members at all.");

        Assert.True(problems.Count == 0,
            "design.md §10: \"No presigned URLs. Downloads always stream through the API: a presigned URL " +
            "would bypass both page restrictions (`canView`) and the audit log.\" A member whose name " +
            "suggests minting a URL/URI/presigned link must not exist on the storage surface — if the name " +
            "is an innocent coincidence, rename it rather than weakening this tripwire. Problems found:\n" +
            string.Join('\n', problems));
    }

    [Fact]
    public void IFileStorage_IsExactlyTheFourStreamingMembers()
    {
        var problems = new List<string>();

        string[] expected = ["SaveAsync", "OpenReadAsync", "DeleteAsync", "ExistsAsync"];
        // GetMembers, not GetMethods: a property or event smuggled onto the
        // interface must trip this too.
        var actual = typeof(IFileStorage).GetMembers().Select(m => m.Name).ToList();

        foreach (var missing in expected.Except(actual))
        {
            problems.Add($"IFileStorage no longer declares '{missing}'.");
        }

        foreach (var extra in actual.Except(expected))
        {
            problems.Add($"IFileStorage declares unexpected member '{extra}'.");
        }

        Assert.True(problems.Count == 0,
            "IFileStorage must be exactly { SaveAsync, OpenReadAsync, DeleteAsync, ExistsAsync } — the " +
            "streaming surface design.md §10 specifies. It is deliberately this narrow because every read " +
            "flows through the API's attachment routes, which enforce `canView` and write the §7 audit row " +
            "before streaming; any member that hands out a reference to the object itself (a presigned URL, " +
            "a direct link, a raw path) bypasses both. Growing this interface is a design.md change first, " +
            "code second. Problems found:\n" + string.Join('\n', problems));
    }
}
