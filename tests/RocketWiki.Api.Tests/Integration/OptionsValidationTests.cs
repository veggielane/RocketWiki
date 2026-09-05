using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// The options families the API binds are validated at startup
/// (<c>ValidateDataAnnotations().ValidateOnStart()</c>): attachment, avatar and emoji
/// upload caps, the co-editing relay caps, file storage, the shared <c>Ai</c> tuning
/// section, the instance identity and the Keycloak settings. Before this, a cap
/// configured as <c>0</c> bound silently and turned every upload into a 413 — a
/// configuration mistake that only showed up as a user-visible failure, in production,
/// on the one path nobody exercises in a smoke test. The AI keys had the same shape of
/// hole: a zero batch size embedded nothing forever, a zero poll interval silently fell
/// back to the default, a zero question cap refused every ask.
///
/// Both halves matter and both are asserted here: a bad value must fail the host, and
/// <b>every default must satisfy its own annotation</b>, because "no configuration at
/// all" is a supported state (design.md §15) and validation that broke it would be worse
/// than no validation.
///
/// One host for all eight families rather than one per family, deliberately: starting a
/// host is the expensive, concurrency-sensitive part of this test project, and
/// <c>StartupValidator</c> collects every failure into one AggregateException — so a
/// single boot proves all eight are wired AND that none of them short-circuits the others.
/// </summary>
public sealed class OptionsValidationTests(RocketWikiApiFactory factory) : IClassFixture<RocketWikiApiFactory>
{
    [Fact]
    public async Task WithNoUploadOrCoEditConfiguration_TheHostStartsAndServes()
    {
        // The shared fixture configures a database and a storage root and nothing else -
        // no Attachments, Avatars, Emojis or CoEdit keys exist in it. If any default
        // violated its own DataAnnotations the host would not start at all, and every
        // other integration test in this project would fail with it.
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void EveryValidatedOptionsFamilyRefusesABadValueAtStartup()
    {
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Attachments:MaxSizeBytes"] = "0",
                    ["Avatars:MaxSizeBytes"] = "-1",
                    ["Emojis:MaxSizeBytes"] = "0",
                    ["CoEdit:UpdateMaxBytes"] = "0",
                    // The Ai tuning keys: one from each of the two features plus a nullable
                    // one, whose 0 used to mean "fall back to the default" and now means
                    // what a 0 means everywhere else in this file.
                    ["Ai:BatchSize"] = "0",
                    ["Ai:ChatTimeoutSeconds"] = "-5",
                    ["Ai:PollSeconds"] = "0",
                    // A blank id would compare every space's origin against nothing — the
                    // silent fail-open the Helm values schema also refuses (design.md §12).
                    ["Instance:Id"] = "",
                    // A blank realm derives an authority ending in /realms/; a scheme-less
                    // authority can never fetch discovery metadata. Both boot-and-then-
                    // reject-every-sign-in failures, moved to boot time.
                    ["Keycloak:Realm"] = "",
                    ["Keycloak:Authority"] = "keycloak.internal/realms/x",
                    // Nested sections are not reached by DataAnnotations' own recursion,
                    // which is why FileStorageOptions validates them explicitly - this
                    // row is what fails if that IValidatableObject is ever dropped as
                    // redundant. A host:port with no scheme is the classic paste error,
                    // and it used to surface as an opaque AWS SDK failure on first use.
                    ["FileStorage:S3:ServiceUrl"] = "minio:9000",
                })));

        // Starting the host is what runs ValidateOnStart; the failure surfaces from the
        // first client the factory hands out.
        var thrown = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        var validationFailures = Flatten(thrown).OfType<OptionsValidationException>().ToList();
        var reported = string.Join(" | ", validationFailures.SelectMany(e => e.Failures));

        Assert.NotEmpty(reported);
        Assert.Contains("MaxSizeBytes", reported, StringComparison.Ordinal);
        Assert.Contains("UpdateMaxBytes", reported, StringComparison.Ordinal);
        Assert.Contains("FileStorage:S3:ServiceUrl", reported, StringComparison.Ordinal);
        Assert.Contains("Ai:BatchSize", reported, StringComparison.Ordinal);
        Assert.Contains("Ai:ChatTimeoutSeconds", reported, StringComparison.Ordinal);
        Assert.Contains("Ai:PollSeconds", reported, StringComparison.Ordinal);
        Assert.Contains("Instance:Id", reported, StringComparison.Ordinal);
        Assert.Contains("Keycloak:Realm", reported, StringComparison.Ordinal);
        Assert.Contains("Keycloak:Authority", reported, StringComparison.Ordinal);

        // Every family reported, not just the first one to fail: StartupValidator
        // collects them all, so an operator fixing config sees the whole list at once.
        var families = validationFailures.Select(e => e.OptionsType.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            ["AiOptions", "AttachmentOptions", "AvatarOptions", "CoEditOptions", "EmojiOptions", "FileStorageOptions", "InstanceOptions", "KeycloakOptions"],
            families.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>The exception tree, including AggregateException's siblings — several
    /// options families failing at once is exactly the case under test.</summary>
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
            {
                yield return inner;
            }

            yield break;
        }

        if (exception.InnerException is { } single)
        {
            foreach (var inner in Flatten(single))
            {
                yield return inner;
            }
        }
    }
}
