using System.Net;
using Alfred.Functions.Functions;
using Alfred.Functions.Tests.Support;
using Xunit;

namespace Alfred.Functions.Tests;

public class GmailRedirectFunctionTests
{
    private static FakeHttpResponseData Run(string threadId)
    {
        var response = new GmailRedirectFunction().Run(new FakeHttpRequestData(method: "GET"), threadId);
        return (FakeHttpResponseData)response;
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("ABC-def-42")]
    public void ValidThreadId_ServesARedirectPageToWebGmail(string threadId)
    {
        var original = Environment.GetEnvironmentVariable("Alfred__GmailAccount");
        try
        {
            Environment.SetEnvironmentVariable("Alfred__GmailAccount", null);

            var response = Run(threadId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = response.BodyText;
            var expectedUrl = $"https://mail.google.com/mail/u/scerri.matthew@gmail.com/#all/{threadId}";
            Assert.Contains($"window.location.replace(\"{expectedUrl}\")", html);
            Assert.Contains($"<a href=\"{expectedUrl}\">", html);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Alfred__GmailAccount", original);
        }
    }

    [Fact]
    public void ConfiguredAccount_IsUsedInTheGmailUrl()
    {
        var original = Environment.GetEnvironmentVariable("Alfred__GmailAccount");
        try
        {
            Environment.SetEnvironmentVariable("Alfred__GmailAccount", "someone@else.com");

            var response = Run("t1");

            Assert.Contains("https://mail.google.com/mail/u/someone@else.com/#all/t1", response.BodyText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Alfred__GmailAccount", original);
        }
    }

    [Theory]
    [InlineData("abc$123")]
    [InlineData("a b")]
    [InlineData("..%2Fetc")]
    [InlineData("<script>")]
    public void InvalidThreadId_IsRejectedWith400(string threadId)
    {
        var response = Run(threadId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
