using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Alfred.Functions.Tests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body)
{
    public string Path => Uri.AbsolutePath;
    public string Query => Uri.Query;
}

// HTTP-layer fake: the real SDK request-building and response-parsing code runs;
// only the wire is replaced. Responses come from an ordered queue first, then a
// route-based responder; an unmatched request fails the test loudly.
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public IEnumerable<RecordedRequest> RequestsTo(string pathFragment) =>
        Requests.Where(r => r.Path.Contains(pathFragment, StringComparison.Ordinal));

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _queue.Enqueue(_ => JsonResponse(json, status));

    public void EnqueueResponder(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _queue.Enqueue(responder);

    // Route matched on "METHOD path-fragment", e.g. "GET /gmail/v1/users/me/messages"
    public void Route(string methodAndPathFragment, Func<HttpRequestMessage, string> jsonFor,
        HttpStatusCode status = HttpStatusCode.OK) =>
        RouteResponder(methodAndPathFragment, req => JsonResponse(jsonFor(req), status));

    public void RouteResponder(string methodAndPathFragment, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var separator = methodAndPathFragment.IndexOf(' ');
        var method = methodAndPathFragment[..separator];
        var fragment = methodAndPathFragment[(separator + 1)..];
        _routes.Add((
            req => req.Method.Method == method
                && req.RequestUri!.AbsolutePath.Contains(fragment, StringComparison.Ordinal),
            responder));
    }

    public void Route(string methodAndPathFragment, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Route(methodAndPathFragment, _ => json, status);

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

        if (_queue.Count > 0)
            return _queue.Dequeue()(request);

        foreach (var (match, respond) in _routes)
        {
            if (match(request))
                return respond(request);
        }

        throw new InvalidOperationException(
            $"No canned response for {request.Method} {request.RequestUri}. Configure a Route or EnqueueJson for it.");
    }
}

// Lets Google.Apis services (Gmail, Calendar) run over the fake handler
internal sealed class FakeGoogleHttpClientFactory(HttpMessageHandler handler) : Google.Apis.Http.HttpClientFactory
{
    protected override HttpMessageHandler CreateHandler(Google.Apis.Http.CreateHttpClientArgs args) => handler;
}

// One-shot loopback HTTP server for code that news up its own HttpClient (the RFC 8058
// one-click unsubscribe POST). Accepts a single request, records it, answers with the
// configured status, then closes. No external network involved.
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _serving;

    public int ResponseStatusCode { get; set; } = 200;
    public string Url { get; }
    public volatile string? RequestText;

    public LoopbackHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}/unsubscribe";
        _serving = Task.Run(ServeOneRequestAsync);
    }

    private async Task ServeOneRequestAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        client.ReceiveTimeout = 10_000;
        using var stream = client.GetStream();

        var received = new StringBuilder();
        var buffer = new byte[16 * 1024];
        var contentLength = -1;

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
                break;
            received.Append(Encoding.UTF8.GetString(buffer, 0, read));

            var text = received.ToString();
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
                continue;

            if (contentLength < 0)
            {
                var lengthLine = text[..headerEnd]
                    .Split("\r\n")
                    .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                contentLength = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
            }

            if (text.Length - headerEnd - 4 >= contentLength)
                break;
        }

        RequestText = received.ToString();

        var reason = ResponseStatusCode == 200 ? "OK" : "Error";
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {ResponseStatusCode} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Listener stopped before a request arrived — fine for negative-path tests
        }
    }
}
