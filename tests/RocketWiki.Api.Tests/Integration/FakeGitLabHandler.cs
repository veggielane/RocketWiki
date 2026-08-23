using System.Net;
using System.Text;

namespace RocketWiki.Api.Tests.Integration;

/// <summary>
/// Stands in for GitLab in the SQLite tier (design.md §14: no live GitLab exists
/// anywhere near these tests): a programmable <see cref="HttpMessageHandler"/>
/// swapped in as the gitlab named client's primary handler, so everything above
/// it — GitLabHttpClient's URL building, header placement, status mapping, size
/// cap — is the real production code.
///
/// Records every request's URI and PRIVATE-TOKEN header, which is what the
/// credential-isolation tests assert against: the token GitLab *saw* is the only
/// ground truth for "user A's token was never used for user B".
/// </summary>
public sealed class FakeGitLabHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(Uri Uri, string? PrivateToken);

    private readonly List<RecordedRequest> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage>? _responder;
    private Exception? _throwOnSend;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public void Reset()
    {
        lock (_requests)
        {
            _requests.Clear();
        }

        _responder = null;
        _throwOnSend = null;
    }

    /// <summary>Every subsequent request gets this responder's answer.</summary>
    public void RespondWith(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _throwOnSend = null;
        _responder = responder;
    }

    public void RespondWithJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        RespondWith(_ => JsonResponse(json, statusCode));

    public void RespondWithStatus(HttpStatusCode statusCode) =>
        RespondWith(_ => new HttpResponseMessage(statusCode) { Content = new StringContent("{}") });

    /// <summary>Simulates GitLab being unreachable (connection refused / DNS failure).</summary>
    public void FailWith(Exception exception)
    {
        _responder = null;
        _throwOnSend = exception;
    }

    /// <summary>A raw-file response with the X-Gitlab-* metadata headers the real
    /// endpoint sends. <paramref name="declaredSize"/> defaults to the body length;
    /// pass an explicit value to test the header-based cap short-circuit.</summary>
    public void RespondWithRawFile(
        byte[] body, string fileName, string filePath, string? refName = null, long? declaredSize = null)
    {
        RespondWith(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            response.Headers.TryAddWithoutValidation("X-Gitlab-File-Name", fileName);
            response.Headers.TryAddWithoutValidation("X-Gitlab-File-Path", filePath);
            response.Headers.TryAddWithoutValidation("X-Gitlab-Size",
                (declaredSize ?? body.Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.TryAddWithoutValidation("X-Gitlab-Content-Sha256", new string('a', 64));
            response.Headers.TryAddWithoutValidation("X-Gitlab-Last-Commit-Id", "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
            if (refName is not null)
            {
                response.Headers.TryAddWithoutValidation("X-Gitlab-Ref", refName);
            }

            return response;
        });
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.TryGetValues("PRIVATE-TOKEN", out var values) ? values.FirstOrDefault() : null;
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.RequestUri!, token));
        }

        if (_throwOnSend is not null)
        {
            throw _throwOnSend;
        }

        var responder = _responder
            ?? throw new InvalidOperationException("FakeGitLabHandler received a request but no responder was configured.");
        return Task.FromResult(responder(request));
    }
}
