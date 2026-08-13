using System.Net;
using System.Text;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;

namespace Kahuna.Storage.Gcs.Tests;

/// <summary>
/// A scripted HTTP transport standing in for the storage service.
/// <para>
/// This exists because two behaviours the adapter depends on cannot be observed through the local
/// emulator: it truncates a listing at the requested page size without ever returning a page token, so
/// no amount of data makes it paginate, and it ignores generation preconditions entirely, accepting a
/// create-if-absent write over an object that already exists. Testing either against the emulator would
/// assert nothing while looking like coverage.
/// </para>
/// <para>
/// So those are pinned at the protocol instead: what request the adapter sends, and what it does with
/// the response the real service would return. That is narrower than an end-to-end test and it is
/// stated as such — it verifies the adapter's half of the exchange, not Google's.
/// </para>
/// </summary>
internal sealed class StubTransport : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    internal List<HttpRequestMessage> Requests { get; } = [];

    internal StubTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Answers the opening leg of an upload, or returns null when the request is something else.
    /// <para>
    /// Every upload this client makes is a resumable one — the .NET storage client has no
    /// single-request upload path, even for a few hundred bytes of manifest — so a stub that only
    /// answered the object write would strand the handshake. The session is opened with a
    /// <c>Location</c>, and the content arrives on the follow-up request.
    /// </para>
    /// </summary>
    internal static HttpResponseMessage? TryOpenUploadSession(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.Query.Contains("uploadType=resumable") != true)
            return null;

        HttpResponseMessage response = Json(HttpStatusCode.OK, "");
        response.Headers.Location = new Uri("https://storage.invalid/upload-session/1");
        return response;
    }

    /// <summary>
    /// A completed object write, as the service reports it on the final chunk. The reported size is
    /// taken from the request because the client validates the two against each other and rejects an
    /// upload whose acknowledged length does not match what it sent.
    /// </summary>
    internal static HttpResponseMessage UploadedObject(HttpRequestMessage request, string name)
    {
        _ = request;
        return Json(HttpStatusCode.OK, $$"""{"kind":"storage#object","name":"{{name}}"}""");
    }

    /// <summary>Builds a storage client whose every request is served by this transport.</summary>
    internal StorageClient CreateClient()
    {
        StorageClientBuilder builder = new()
        {
            UnauthenticatedAccess = true,
            HttpClientFactory = new StubHttpClientFactory(this)
        };

        return builder.Build();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}

/// <summary>
/// A listing response with an optional continuation token, shaped like the storage service's.
/// </summary>
internal static class StubListing
{
    internal static string Page(IEnumerable<string> names, string? nextPageToken)
    {
        string items = string.Join(",", names.Select(n =>
            $$"""{"kind":"storage#object","name":{{Quote(n)}},"size":"3"}"""));

        string token = nextPageToken is null ? "" : $""","nextPageToken":{Quote(nextPageToken)}""";

        return $$"""{"kind":"storage#objects","items":[{{items}}]{{token}}}""";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
