using Microsoft.Extensions.AI;
using RocketWiki.Api.Assistant;
using RocketWiki.Api.Audit;
using RocketWiki.Api.GraphQL;
using RocketWiki.Api.Identity;
using RocketWiki.Core.Access;
using RocketWiki.Core.Entities;
using RocketWiki.Core.Enums;
using RocketWiki.Core.Services;
using Xunit;

namespace RocketWiki.Api.Tests;

/// <summary>
/// design.md §7 on the one path where content crosses the §9.4 boundary: an ask that
/// THROWS must still be audited.
///
/// <para><c>askWiki</c> is the only surface that sends the caller's question and page
/// content to an outside endpoint. The audit write used to sit on the success path, so
/// anything that unwound past it — a browser navigating away mid-generation, which
/// rethrows <c>OperationCanceledException</c> by design, or a throw out of retrieval such
/// as the deliberately-propagating vector-dimension mismatch — meant the content had
/// already left with <b>zero record</b> of who asked what or which pages travelled.</para>
///
/// <para>Exercised at the resolver rather than over HTTP because the trigger is an
/// exception escaping <c>AskAsync</c>, and the integration fixture cannot produce one: its
/// chat-client failure hook is caught and degraded to UNREACHABLE, and cancelling
/// Hot Chocolate's own request token from a test is not something a fake can reach. A
/// throwing <c>ISearchService</c> is the honest, reachable version of the same unwind.</para>
/// </summary>
public sealed class AskWikiAbandonedAuditTests
{
    private sealed class ThrowingSearchService(Exception toThrow) : ISearchService
    {
        public Task<IReadOnlyList<SearchHit>> SearchAsync(
            SearchRequest request, Principal principal, int maxResults, CancellationToken cancellationToken) =>
            throw toThrow;
    }

    private sealed class UnusedPageReadService : IPageReadService
    {
        public Task<ReadResult<Page>> GetPageAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("retrieval threw first; this must never be reached.");

        public Task<Guid?> FindPageIdBySlugAsync(string spaceKey, string slug, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PageAccess> GetPageAccessAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, PageAccess>> GetPageAccessBatchAsync(
            IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadResult<IReadOnlyList<PageTreeEntry>>> GetPageTreeAsync(Guid spaceId, Principal principal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReadResult<IReadOnlyList<PageRevision>>> GetRevisionHistoryAsync(Guid pageId, Principal principal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, Page>> GetPagesAsync(
            IReadOnlyCollection<Guid> pageIds, Principal principal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetVisibleChildIdsAsync(
            IReadOnlyCollection<Guid> parentPageIds, Principal principal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedMarkingReader : IPageMarkingReader
    {
        public Task<IReadOnlyDictionary<Guid, ProtectiveMarking>> LoadAsync(
            IReadOnlyCollection<Guid> pageIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingAuditSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(AuditRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class StubPrincipalAccessor(Principal? principal) : ICurrentPrincipalAccessor
    {
        public Principal? Current { get; } = principal;
    }

    private sealed class StubActingUserAccessor(Guid? userId) : IActingUserAccessor
    {
        public Guid? ActingUserId { get; set; } = userId;
    }

    private static AskWikiService ServiceThatThrows(Exception toThrow, IAuditSink sink) =>
        new(new ThrowingSearchService(toThrow),
            new UnusedPageReadService(),
            new UnusedMarkingReader(),
            sink,
            SelectorCatalog.Empty,
            new AssistantOptions("fake-model", TimeSpan.FromSeconds(5), MaxContextChars: 24_000, MaxRetrievedPages: 8),
            new UnusedChatClient());

    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("retrieval threw first; the model must never be called.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task AnAskThatThrows_StillWritesItsAuditRow_WithTheQuestion(Type exceptionType)
    {
        var sink = new CapturingAuditSink();
        var thrown = (Exception)Activator.CreateInstance(exceptionType)!;
        var query = new Query();
        const string question = "what is the ullage pressure procedure?";

        await Assert.ThrowsAsync(exceptionType, () => query.AskWiki(
            question,
            ServiceThatThrows(thrown, sink),
            new StubPrincipalAccessor(Principal.Create("asker-sub", [])),
            new StubActingUserAccessor(Guid.NewGuid()),
            sink,
            CancellationToken.None));

        // The throw propagates — the caller still sees the failure — AND the row exists.
        var record = Assert.Single(sink.Records);
        Assert.Equal("assistant.ask", record.Action);
        Assert.Equal(AuditOutcome.Success, record.Outcome);
        Assert.Contains(question, record.DetailsJson);
        Assert.Contains("\"disposition\":\"abandoned\"", record.DetailsJson);
    }

    /// <summary>
    /// Non-vacuous the other way: a normal ask still writes exactly one row, and it is not
    /// labelled abandoned. Without this the test above would pass against an implementation
    /// that labelled everything abandoned.
    /// </summary>
    [Fact]
    public async Task AnAskThatCompletes_IsNotLabelledAbandoned()
    {
        var sink = new CapturingAuditSink();
        var query = new Query();

        // No options and no chat client — the NOT_CONFIGURED disposition, which is a
        // completed ask that returns rather than throwing.
        var service = new AskWikiService(
            new ThrowingSearchService(new InvalidOperationException("never reached")),
            new UnusedPageReadService(),
            new UnusedMarkingReader(),
            sink,
            SelectorCatalog.Empty);

        var payload = await query.AskWiki(
            "anything",
            service,
            new StubPrincipalAccessor(Principal.Create("asker-sub", [])),
            new StubActingUserAccessor(Guid.NewGuid()),
            sink,
            CancellationToken.None);

        Assert.Equal(AskWikiUnavailableReason.NotConfigured, payload.Unavailable);

        var record = Assert.Single(sink.Records);
        Assert.DoesNotContain("abandoned", record.DetailsJson);
        Assert.Contains("not_configured", record.DetailsJson);
    }
}
