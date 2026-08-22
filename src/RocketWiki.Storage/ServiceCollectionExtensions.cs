using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RocketWiki.Storage;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IFileStorage"/> from the "FileStorage" configuration
    /// section (design.md §10), selecting the concrete provider by
    /// <c>FileStorage:Provider</c>. Unset/empty means "FileSystem" (the local-dev
    /// default); any other unrecognized value fails fast at startup rather than
    /// silently picking a provider nobody asked for.
    /// </summary>
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(FileStorageOptions.SectionName);
        services.Configure<FileStorageOptions>(section);

        var provider = section["Provider"];

        switch (provider)
        {
            case null or "":
            case "FileSystem":
                services.AddSingleton<IFileStorage, FileSystemFileStorage>();
                break;

            case "S3":
                services.AddSingleton<IAmazonS3>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value.S3
                        ?? throw new InvalidOperationException(
                            "FileStorage:S3 must be configured when FileStorage:Provider is 'S3'.");

                    var s3Config = new AmazonS3Config
                    {
                        ForcePathStyle = options.ForcePathStyle,
                    };

                    if (!string.IsNullOrEmpty(options.ServiceUrl))
                    {
                        s3Config.ServiceURL = options.ServiceUrl;
                    }

                    if (!string.IsNullOrEmpty(options.Region))
                    {
                        s3Config.AuthenticationRegion = options.Region;
                    }

                    return string.IsNullOrEmpty(options.AccessKey)
                        ? new AmazonS3Client(s3Config)
                        : new AmazonS3Client(options.AccessKey, options.SecretKey, s3Config);
                });
                services.AddSingleton<IFileStorage, S3FileStorage>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unrecognized FileStorage:Provider '{provider}'. Expected 'FileSystem' or 'S3'.");
        }

        return services;
    }
}
