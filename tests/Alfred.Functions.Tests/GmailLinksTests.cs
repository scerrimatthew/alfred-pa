using Alfred.Functions.Services.Gmail;
using Xunit;

namespace Alfred.Functions.Tests;

public class GmailLinksTests
{
    [Fact]
    public void ForThread_WithoutOverride_UsesProductionFunctionAppUrl()
    {
        var original = Environment.GetEnvironmentVariable("Alfred__PublicBaseUrl");
        try
        {
            Environment.SetEnvironmentVariable("Alfred__PublicBaseUrl", null);

            Assert.Equal(
                "https://func-matt-scerri-alfred-prod-westeu-001.azurewebsites.net/api/open/thread-42",
                GmailLinks.ForThread("thread-42"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Alfred__PublicBaseUrl", original);
        }
    }

    [Fact]
    public void ForThread_WithOverride_UsesItAndTrimsTrailingSlash()
    {
        var original = Environment.GetEnvironmentVariable("Alfred__PublicBaseUrl");
        try
        {
            Environment.SetEnvironmentVariable("Alfred__PublicBaseUrl", "https://example.com/");

            Assert.Equal("https://example.com/api/open/t1", GmailLinks.ForThread("t1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Alfred__PublicBaseUrl", original);
        }
    }
}
