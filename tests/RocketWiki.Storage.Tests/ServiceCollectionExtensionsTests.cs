using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// Covers provider selection in <see cref="ServiceCollectionExtensions.AddFileStorage"/>
/// (design.md §10). Only wiring/selection is tested here — actually exercising the
/// resulting S3FileStorage against a live bucket needs MinIO, which this suite does
/// not stand up.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void UnsetProvider_DefaultsToFileSystem()
    {
        var config = BuildConfig(new()
        {
            ["FileStorage:FileSystem:Root"] = Path.GetTempPath(),
        });

        var services = new ServiceCollection().AddFileStorage(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileSystemFileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void ExplicitFileSystemProvider_RegistersFileSystemFileStorage()
    {
        var config = BuildConfig(new()
        {
            ["FileStorage:Provider"] = "FileSystem",
            ["FileStorage:FileSystem:Root"] = Path.GetTempPath(),
        });

        var services = new ServiceCollection().AddFileStorage(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileSystemFileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void S3Provider_RegistersS3FileStorageAndAmazonS3Client()
    {
        var config = BuildConfig(new()
        {
            ["FileStorage:Provider"] = "S3",
            ["FileStorage:S3:ServiceUrl"] = "http://localhost:9000",
            ["FileStorage:S3:Bucket"] = "rocketwiki",
            ["FileStorage:S3:ForcePathStyle"] = "true",
        });

        var services = new ServiceCollection().AddFileStorage(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<S3FileStorage>(provider.GetRequiredService<IFileStorage>());
        Assert.NotNull(provider.GetRequiredService<IAmazonS3>());
    }

    [Fact]
    public void SqlServerProvider_RegistersSqlServerFileStorage()
    {
        var config = BuildConfig(new()
        {
            ["FileStorage:Provider"] = "SqlServer",
            ["FileStorage:SqlServer:ConnectionString"] = "Server=blobs;Database=blobs",
        });

        var services = new ServiceCollection().AddFileStorage(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqlServerFileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void SqlServerProvider_WithNoSectionOfItsOwn_FallsBackToTheApplicationDatabase()
    {
        // Selecting the provider and configuring nothing else is the shape design.md §10
        // documents: blobs land in the application database, which is the only reason to
        // accept them into a database at all. The registration must therefore reach the
        // configuration root, not just the bound FileStorage section.
        var config = BuildConfig(new()
        {
            ["FileStorage:Provider"] = "SqlServer",
            ["ConnectionStrings:rocketwiki"] = "Server=app;Database=app",
        });

        var services = new ServiceCollection().AddFileStorage(config);
        using var provider = services.BuildServiceProvider();

        var storage = Assert.IsType<SqlServerFileStorage>(provider.GetRequiredService<IFileStorage>());
        Assert.Equal("Server=app;Database=app", storage.ConnectionString);
    }

    [Fact]
    public void UnrecognizedProvider_ThrowsAtRegistration()
    {
        var config = BuildConfig(new()
        {
            ["FileStorage:Provider"] = "Dropbox",
        });

        var thrown = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFileStorage(config));

        // The message is an operator's only clue at boot, so it must list every provider
        // that exists - a switch that grew a case without updating it is the failure mode.
        Assert.Contains("'FileSystem', 'S3' or 'SqlServer'", thrown.Message, StringComparison.Ordinal);
    }
}
