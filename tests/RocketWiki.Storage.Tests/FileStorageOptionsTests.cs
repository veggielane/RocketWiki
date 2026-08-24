using System.ComponentModel.DataAnnotations;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// The annotations FileStorageOptions carries, exercised directly rather than through a
/// host boot. RocketWiki.Api.Tests' OptionsValidationTests proves the family is wired to
/// <c>ValidateDataAnnotations().ValidateOnStart()</c> and that a bad nested value fails
/// the host; this covers the vocabulary itself — including that unset stays a supported
/// state (design.md §10: an empty section selects FileSystem).
/// </summary>
public sealed class FileStorageOptionsTests
{
    private static List<ValidationResult> Validate(FileStorageOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FileSystem")]
    [InlineData("S3")]
    [InlineData("SqlServer")]
    public void EveryProviderTheSwitchAccepts_PassesTheAnnotation(string? provider)
    {
        // The annotation and AddFileStorage's switch are two statements of one rule; a
        // provider the switch registers but the annotation rejects would fail the host at
        // boot for a configuration that is actually valid.
        Assert.Empty(Validate(new FileStorageOptions { Provider = provider }));
    }

    [Fact]
    public void UnrecognizedProvider_FailsWithAMessageListingAllThree()
    {
        var results = Validate(new FileStorageOptions { Provider = "Dropbox" });

        var message = Assert.Single(results).ErrorMessage;
        Assert.NotNull(message);
        Assert.Contains("'FileSystem', 'S3' or 'SqlServer'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedSectionsAreReached_IncludingSqlServer()
    {
        // DataAnnotations does not recurse into complex sub-objects; FileStorageOptions
        // walks them itself. The S3 row is the sentinel that the recursion still runs (it
        // is the only nested section carrying an annotation today), and the SqlServer
        // section is walked alongside it - deliberately annotation-free, since an unset or
        // empty connection string is the supported "share the application database" state.
        var results = Validate(new FileStorageOptions
        {
            Provider = "SqlServer",
            S3 = new S3FileStorageOptions { ServiceUrl = "minio:9000" },
            SqlServer = new SqlServerFileStorageOptions { ConnectionString = null },
        });

        var messages = results.Select(r => r.ErrorMessage ?? string.Empty).ToList();
        Assert.Contains(messages, m => m.Contains("FileStorage:S3: ", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("FileStorage:SqlServer", StringComparison.Ordinal));
    }

    [Fact]
    public void APopulatedSqlServerSection_ValidatesCleanly()
    {
        Assert.Empty(Validate(new FileStorageOptions
        {
            Provider = "SqlServer",
            SqlServer = new SqlServerFileStorageOptions
            {
                ConnectionString = "Server=blobs;Database=blobs;Integrated Security=true",
            },
        }));
    }
}
