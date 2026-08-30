using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RocketWiki.Storage;

/// <summary>
/// Operational instrumentation for attachment blob storage (design.md §15). Unlike the
/// database, object storage has no built-in instrumentation reaching this process — the
/// AWS SDK's own tracing isn't wired up and the filesystem provider has none by
/// definition — so without these spans an attachment upload is a hole in the trace
/// between the HTTP span and the audit row. The SqlServer provider is the one exception
/// (SqlClient's instrumentation sees its commands), and it is still wrapped here so a
/// storage dashboard reads the same whichever provider is configured.
///
/// **Storage keys are never tagged.** They are opaque
/// (<c>attachments/{yyyy}/{MM}/{guid}</c>) and therefore not content, but nothing on a
/// latency dashboard needs them, and design.md §15's rule is to carry the minimum that
/// diagnoses a fault. Provider, operation, outcome and byte count do that; the key does
/// not. File names, content types, and page titles are all absent for the stronger
/// reason that they are user content.
/// </summary>
public static class StorageTelemetry
{
    public const string SourceName = "RocketWiki.Storage";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("rocketwiki.storage.operation.duration", "s",
            "Duration of a blob storage operation, by provider, operation and outcome.");

    public static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("rocketwiki.storage.operations", "{operation}",
            "Blob storage operations, by provider, operation and outcome.");

    public static readonly Histogram<long> OperationBytes =
        Meter.CreateHistogram<long>("rocketwiki.storage.operation.bytes", "By",
            "Bytes read or written by a blob storage operation, where the size is known.");

    public const string ProviderTag = "rocketwiki.storage.provider";
    public const string OperationTag = "rocketwiki.storage.operation";
    public const string OutcomeTag = "rocketwiki.storage.outcome";
    public const string BytesTag = "rocketwiki.storage.bytes";

    /// <summary>Fixed outcome tag for a caller that went away mid-operation — not a fault.</summary>
    public const string CanceledOutcome = "canceled";

    public const string FileSystemProvider = "filesystem";
    public const string S3Provider = "s3";
    public const string SqlServerProvider = "sqlserver";

    public const string SaveOperation = "save";
    public const string OpenReadOperation = "open_read";
    public const string DeleteOperation = "delete";
    public const string ExistsOperation = "exists";

    /// <summary>
    /// One span plus one duration/count sample per storage call. Returned as a disposable
    /// so both providers share a single try/finally rather than repeating it eight times;
    /// the caller sets <see cref="StorageOperation.Bytes"/> when a size is known and
    /// <see cref="StorageOperation.Fail"/> is handled automatically by the exception path.
    /// </summary>
    public static StorageOperation StartOperation(string provider, string operation) =>
        new(provider, operation);

    public sealed class StorageOperation : IDisposable
    {
        private readonly string _provider;
        private readonly string _operation;
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private string _outcome = "success";

        internal StorageOperation(string provider, string operation)
        {
            _provider = provider;
            _operation = operation;
            _startTimestamp = Stopwatch.GetTimestamp();
            _activity = ActivitySource.StartActivity($"rocketwiki.storage.{operation}", ActivityKind.Client);
            _activity?.SetTag(ProviderTag, provider);
            _activity?.SetTag(OperationTag, operation);
        }

        /// <summary>Byte count, when the provider can learn it without consuming the stream.</summary>
        public long? Bytes { get; set; }

        /// <summary>
        /// Records a non-exceptional outcome that still isn't a plain success — an
        /// <c>ExistsAsync</c> that found nothing, for instance. The tag is a fixed
        /// vocabulary, never a message.
        /// </summary>
        public void SetOutcome(string outcome) => _outcome = outcome;

        public void Fail(Exception exception)
        {
            // A client disconnecting mid-download is not a storage fault. It arrived
            // here as outcome "TaskCanceledException" with span status Error, so a
            // dashboard counted every abandoned download — a user navigating away from
            // a large attachment — as a provider error. Given the error rate is what an
            // operator watches to decide whether the object store is healthy, that is
            // noise in exactly the signal that must stay trustworthy.
            if (exception is OperationCanceledException)
            {
                _outcome = CanceledOutcome;
                return;
            }

            _outcome = exception.GetType().Name;
            _activity?.SetStatus(ActivityStatusCode.Error);
            // The type name only: an IOException's message carries the full filesystem
            // path, and an AmazonS3Exception's carries bucket and key (design.md §15).
            _activity?.SetTag("error.type", exception.GetType().FullName);
        }

        public void Dispose()
        {
            var tags = new TagList
            {
                { ProviderTag, _provider },
                { OperationTag, _operation },
                { OutcomeTag, _outcome },
            };

            OperationDuration.Record(Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds, tags);
            Operations.Add(1, tags);

            if (Bytes is { } bytes)
            {
                OperationBytes.Record(bytes, tags);
                _activity?.SetTag(BytesTag, bytes);
            }

            _activity?.SetTag(OutcomeTag, _outcome);
            _activity?.Dispose();
        }
    }
}
