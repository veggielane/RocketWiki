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
    public void UnrecognizedProvider_ThrowsAtRegistration()
    {
        var config = BuildConfig(new()
        {
            ["FileStorage:Provider"] = "Dropbox",
        });

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddFileStorage(config));
    }
}
