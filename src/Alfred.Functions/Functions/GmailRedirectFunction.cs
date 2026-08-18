using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Alfred.Functions.Functions;

// Telegram's in-app browser ignores iOS universal links, so mail.google.com URLs open in the
// browser instead of the Gmail app. This endpoint serves a page that jumps to the app's native
// URL scheme (which a page CAN invoke, unlike a Telegram link) with a web fallback.
public partial class GmailRedirectFunction
{
    [Function("GmailRedirect")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "open/{threadId}")] HttpRequestData req,
        string threadId)
    {
        if (!ThreadIdRegex().IsMatch(threadId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // accountId by email is unambiguous — an index (accountId=1) targets whichever account
        // happens to be first in the Gmail app and silently lands on the inbox when wrong
        var account = Environment.GetEnvironmentVariable("Alfred__GmailAccount") ?? "scerri.matthew@gmail.com";
        var webUrl = $"https://mail.google.com/mail/u/0/#all/{threadId}";
        var appUrl = $"googlegmail:///cv={threadId}/accountId={account}";

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.WriteString($$"""
            <!DOCTYPE html>
            <html>
            <head><meta name="viewport" content="width=device-width, initial-scale=1"><title>Opening Gmail…</title></head>
            <body style="font-family:-apple-system,Segoe UI,sans-serif;text-align:center;padding-top:4em;color:#444">
            <p>Opening Gmail…</p>
            <p><a href="{{webUrl}}">Open in Gmail on the web instead</a></p>
            <script>
              window.location.href = "{{appUrl}}";
              setTimeout(function () { window.location.href = "{{webUrl}}"; }, 1600);
            </script>
            </body>
            </html>
            """);
        return response;
    }

    [GeneratedRegex("^[a-zA-Z0-9-]+$")]
    private static partial Regex ThreadIdRegex();
}
