using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace RocketWiki.Storage.Tests;

/// <summary>
/// design.md §15/§16: proves <see cref="StorageTelemetry"/>'s span and instruments
/// actually fire, against the real <see cref="FileSystemFileStorage"/> doing real disk
/// I/O — the same tier the rest of this project's coverage uses.
///
/// S3FileStorage's instrumentation is written the same way but is NOT proven here, for
/// the same reason the rest of that provider isn't: it needs a live S3-compatible
/// endpoint this suite doesn't stand up.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class StorageTelemetryTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemFileStorage _storage;

    public StorageTelemetryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rocketwiki-storage-telemetry-" + Guid.NewGuid());
        _storage = new FileSystemFileStorage(Options.Create(new FileStorageOptions
        {
            FileSystem = new FileSystemFileStorageOptions { Root = _root },
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Captures every activity from RocketWiki.Storage for the lifetime of the listener.
    /// <c>Sample</c> must return <c>AllData</c> — the default with no listener registered
    /// is to create nothing at all, which is exactly why these spans cost nothing in a
    /// process with no exporter.
    /// </summary>
    private static (ActivityListener Listener, List<Activity> Captured) ListenToStorage()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StorageTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, captured);
    }

    private static Stream Utf8Stream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Save_EmitsASpanTaggedWithProviderOperationAndByteCount()
    {
        var (listener, captured) = ListenToStorage();
        using (listener)
        {
            await _storage.SaveAsync("attachments/2026/08/telemetry.txt", Utf8Stream("twelve bytes"), "text/plain", default);
        }

        var span = Assert.Single(captured);
        Assert.Equal("rocketwiki.storage.save", span.OperationName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal(StorageTelemetry.FileSystemProvider, span.GetTagItem(StorageTelemetry.ProviderTag));
        Assert.Equal(StorageTelemetry.SaveOperation, span.GetTagItem(StorageTelemetry.OperationTag));
        Assert.Equal(12L, span.GetTagItem(StorageTelemetry.BytesTag));
        Assert.Equal("success", span.GetTagItem(StorageTelemetry.OutcomeTag));
    }

    [Fact]
    public async Task OpenRead_EmitsASpanCarryingTheBytesItIsAboutToServe()
    {
        await _storage.SaveAsync("attachments/2026/08/read.txt", Utf8Stream("hello"), "text/plain", default);

        var (listener, captured) = ListenToStorage();
        using (listener)
        {
            await using var stream = await _storage.OpenReadAsync("attachments/2026/08/read.txt", default);
        }

        var span = Assert.Single(captured);
        Assert.Equal("rocketwiki.storage.open_read", span.OperationName);
        Assert.Equal(5L, span.GetTagItem(StorageTelemetry.BytesTag));
    }

    [Fact]
    public async Task MissingKey_MarksTheSpanFailedWithAnErrorTypeAndNoMessage()
    {
        var (listener, captured) = ListenToStorage();
        using (listener)
        {
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => _storage.OpenReadAsync("attachments/2026/08/absent.txt", default));
        }

        var span = Assert.Single(captured);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("System.IO.FileNotFoundException", span.GetTagItem("error.type"));
        Assert.Equal(nameof(FileNotFoundException), span.GetTagItem(StorageTelemetry.OutcomeTag));
    }

    [Fact]
    public async Task Operations_RecordDurationAndCountTaggedByProviderOperationAndOutcome()
    {
        using var duration = new MetricCollector<double>(StorageTelemetry.Meter, "rocketwiki.storage.operation.duration");
        using var operations = new MetricCollector<long>(StorageTelemetry.Meter, "rocketwiki.storage.operations");

        await _storage.SaveAsync("attachments/2026/08/metered.txt", Utf8Stream("x"), "text/plain", default);

        var durationSample = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.True(durationSample.Value >= 0);
        Assert.Equal(StorageTelemetry.FileSystemProvider, durationSample.Tags[StorageTelemetry.ProviderTag]);
        Assert.Equal(StorageTelemetry.SaveOperation, durationSample.Tags[StorageTelemetry.OperationTag]);
        Assert.Equal("success", durationSample.Tags[StorageTelemetry.OutcomeTag]);

        var operationSample = Assert.Single(operations.GetMeasurementSnapshot());
        Assert.Equal(1, operationSample.Value);
    }

    [Fact]
    public async Task Exists_DistinguishesFoundFromNotFoundWithoutThrowing()
    {
        using var operations = new MetricCollector<long>(StorageTelemetry.Meter, "rocketwiki.storage.operations");

        await _storage.SaveAsync("attachments/2026/08/present.txt", Utf8Stream("x"), "text/plain", default);
        Assert.True(await _storage.ExistsAsync("attachments/2026/08/present.txt", default));
        Assert.False(await _storage.ExistsAsync("attachments/2026/08/absent.txt", default));

        var existsOutcomes = operations.GetMeasurementSnapshot()
            .Where(m => Equals(m.Tags[StorageTelemetry.OperationTag], StorageTelemetry.ExistsOperation))
            .Select(m => m.Tags[StorageTelemetry.OutcomeTag])
            .ToList();

        Assert.Equal(["found", "not_found"], existsOutcomes);
    }

    [Fact]
    public async Task SpanTags_NeverCarryTheStorageKeyOrContent()
    {
        // design.md §15. The key is opaque and therefore not content, but nothing on a
        // storage dashboard needs it; the file's bytes are content outright. Both use a
        // distinctive sentinel so a regression here can't hide.
        var (listener, captured) = ListenToStorage();
        using (listener)
        {
            await _storage.SaveAsync(
                "attachments/2026/08/SENTINELKEY.txt", Utf8Stream("SENTINELCONTENT"), "text/plain", default);
        }

        var span = Assert.Single(captured);
        foreach (var (key, value) in span.Tags)
        {
            Assert.DoesNotContain("SENTINEL", $"{key}={value}", StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("SENTINEL", span.DisplayName, StringComparison.OrdinalIgnoreCase);
    }
}
