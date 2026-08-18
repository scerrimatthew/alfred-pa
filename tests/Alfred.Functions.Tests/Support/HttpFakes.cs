using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using NSubstitute;

namespace Alfred.Functions.Tests.Support;

// Minimal concrete HttpRequestData/HttpResponseData for driving HTTP-triggered
// functions in-memory. The worker SDK's CreateResponse(HttpStatusCode) extension
// calls the abstract CreateResponse() below and sets the status code on the result.
internal sealed class FakeHttpRequestData : HttpRequestData
{
    private readonly Stream _body;

    public FakeHttpRequestData(string body = "", string method = "POST")
        : this(Substitute.For<FunctionContext>(), body, method)
    {
    }

    public FakeHttpRequestData(FunctionContext context, string body, string method)
        : base(context)
    {
        _body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Method = method;
    }

    public override Stream Body => _body;
    public override HttpHeadersCollection Headers { get; } = new();
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
    public override Uri Url { get; } = new("https://localhost/api/test");
    public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
    public override string Method { get; }

    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);
}

internal sealed class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext context) : base(context)
    {
    }

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public override HttpHeadersCollection Headers { get; set; } = new();
    public override Stream Body { get; set; } = new MemoryStream();
    public override HttpCookies Cookies => null!;

    public string BodyText
    {
        get
        {
            Body.Position = 0;
            using var reader = new StreamReader(Body, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }
    }
}
