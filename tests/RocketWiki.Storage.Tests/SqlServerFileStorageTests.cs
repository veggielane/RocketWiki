using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// What SqlServerFileStorage can be held to without an engine: connection-string
/// resolution, and that every method refuses a malformed key BEFORE it opens a
/// connection (design.md §10). The trick is the same one S3FileStorageTests uses with a
/// null IAmazonS3 — the storage is deliberately constructed against a server that cannot
/// exist, so if validation ever slipped to after the first connection these tests would
/// fail with a SqlException instead of the ArgumentException they assert.
///
/// The provider's live behavior — round-tripping bytes, the chunked write, the
/// sequential read, first-use table creation — is covered where it can only honestly be
/// covered: tests/RocketWiki.SqlServer.Tests, against a real engine (design.md §14
/// tier 3), which runs in CI and skips visibly without Docker.
/// </summary>
public sealed class SqlServerFileStorageTests
{
    /// <summary>Port 1: nothing listens there, the connect is refused immediately rather
    /// than hanging, and no retry/timeout budget makes these tests slow.</summary>
    private const string NowhereConnectionString =
        "Server=localhost,1;Database=nowhere;User Id=sa;Password=not-a-real-password;" +
        "Encrypt=false;Connect Timeout=1;ConnectRetryCount=0";

    private static IConfiguration EmptyConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static SqlServerFileStorage CreateStorage() => new(
        Options.Create(new FileStorageOptions
        {
            SqlServer = new SqlServerFileStorageOptions { ConnectionString = NowhereConnectionString },
        }),
        EmptyConfiguration());

    private static Stream Payload() => new MemoryStream([1, 2, 3]);

    public static TheoryData<string> MaliciousKeys() => new(
        "",
        "   ",
        "attachments\\2026\\08\\file.bin",
        "..\\outside.txt",
        "C:\\Windows\\System32\\config",
        "/etc/passwd",
        "../outside.txt",
        "attachments/../../outside.txt",
        "attachments/./file.bin");

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task Save_MalformedKey_ThrowsBeforeAnyConnectionIsOpened(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().SaveAsync(key, Payload(), "application/octet-stream", CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task OpenRead_MalformedKey_ThrowsBeforeAnyConnectionIsOpened(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().OpenReadAsync(key, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task Delete_MalformedKey_ThrowsBeforeAnyConnectionIsOpened(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().DeleteAsync(key, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MaliciousKeys))]
    public async Task Exists_MalformedKey_ThrowsBeforeAnyConnectionIsOpened(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateStorage().ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task WellFormedKey_DoesReachTheServer_SoTheRejectionsAboveAreNotVacuous()
    {
        // The positive control for every theory above: with a key that passes validation,
        // the same storage against the same unreachable server fails at the connection -
        // a different exception type entirely. Without this, "throws ArgumentException"
        // could be satisfied by a provider that never talks to anything.
        var thrown = await Record.ExceptionAsync(
            () => CreateStorage().ExistsAsync("attachments/2026/08/well-formed.bin", CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.IsNotType<ArgumentException>(thrown);
    }

    [Fact]
    public void ConnectionString_PrefersTheProviderSection()
    {
        // A separate database for blobs is supported and often wiser than sharing the
        // application's - blob churn otherwise lands in the transaction log carrying page
        // edits - so the explicit setting must win over the fallback, not merge with it.
        var storage = new SqlServerFileStorage(
            Options.Create(new FileStorageOptions
            {
                SqlServer = new SqlServerFileStorageOptions { ConnectionString = "Server=blobs;Database=blobs" },
            }),
            ConfigurationWith(new() { ["ConnectionStrings:rocketwiki"] = "Server=app;Database=app" }));

        Assert.Equal("Server=blobs;Database=blobs", storage.ConnectionString);
    }

    [Fact]
    public void ConnectionString_FallsBackToTheApplicationDatabase()
    {
        // The zero-configuration shape, and the reason to choose this provider at all:
        // blobs live in the same database as content, so one backup and one restore cover
        // both (design.md §10).
        var storage = new SqlServerFileStorage(
            Options.Create(new FileStorageOptions()),
            ConfigurationWith(new() { ["ConnectionStrings:rocketwiki"] = "Server=app;Database=app" }));

        Assert.Equal("Server=app;Database=app", storage.ConnectionString);
    }

    [Fact]
    public void ConnectionString_EmptyProviderSetting_IsTreatedAsUnset()
    {
        // An env var set to "" is how a deployment spells "leave this alone"; it must not
        // become an empty connection string the first upload discovers.
        var storage = new SqlServerFileStorage(
            Options.Create(new FileStorageOptions
            {
                SqlServer = new SqlServerFileStorageOptions { ConnectionString = "   " },
            }),
            ConfigurationWith(new() { ["ConnectionStrings:rocketwiki"] = "Server=app;Database=app" }));

        Assert.Equal("Server=app;Database=app", storage.ConnectionString);
    }

    [Fact]
    public void Constructor_WithNeitherConnectionString_ThrowsNamingBothKeys()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => new SqlServerFileStorage(
            Options.Create(new FileStorageOptions()), EmptyConfiguration()));

        Assert.Contains("FileStorage:SqlServer:ConnectionString", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:rocketwiki", thrown.Message, StringComparison.Ordinal);
    }
}
