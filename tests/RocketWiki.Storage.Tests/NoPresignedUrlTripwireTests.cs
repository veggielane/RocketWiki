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
    /// <summary>
    /// The contract plus <b>every</b> implementation of it in the assembly, discovered
    /// rather than listed. A hard-coded list is the wrong shape for a tripwire: the
    /// thing it guards against is a NEW provider, and a new provider is exactly what a
    /// hard-coded list does not contain. AuditCoverageTests makes the same argument
    /// for its own sweep.
    /// </summary>
    private static Type[] StorageSurface =>
    [
        typeof(IFileStorage),
        .. typeof(IFileStorage).Assembly.GetTypes()
            .Where(t => typeof(IFileStorage).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];

    private static readonly string[] ForbiddenNameFragments = ["url", "uri", "presign", "signedlink"];

    [Fact]
    public void NoStorageMember_MintsUrlsOrPresignedLinks()
    {
        var problems = new List<string>();
        var sweptMembers = 0;
        var declaredMemberNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in StorageSurface)
        {
            const BindingFlags visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            foreach (var member in type.GetMembers(visible))
            {
                sweptMembers++;
                if (member.DeclaringType == type)
                {
                    declaredMemberNames.Add(member.Name);
                }

                foreach (var fragment in ForbiddenNameFragments)
                {
                    if (member.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{type.Name}.{member.Name}: name contains '{fragment}'.");
                    }
                }
            }
        }

        // Non-vacuous, and it has to be asserted on DECLARED members. sweptMembers > 0
        // could never fail: GetMembers without DeclaredOnly always returns the
        // inherited Object members, so the old guard stayed green even if every
        // storage method vanished and the sweep was inspecting ToString and Equals.
        Assert.True(sweptMembers > 0, "Reflection sweep of the storage surface found no members at all.");
        // Discovery really found the providers, not just the interface: a query that
        // silently matched nothing would still satisfy the member check above via
        // IFileStorage alone.
        Assert.True(StorageSurface.Length >= 4,
            $"Expected the interface plus its implementations; discovered {StorageSurface.Length} type(s).");
        foreach (var required in new[] { "SaveAsync", "OpenReadAsync", "DeleteAsync", "ExistsAsync" })
        {
            Assert.Contains(required, declaredMemberNames);
        }

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
