using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;

Console.WriteLine("=== Alfred — Google OAuth2 Refresh Token Setup ===");
Console.WriteLine();

string clientId;
string clientSecret;

if (args.Length >= 2)
{
    clientId = Clean(args[0]);
    clientSecret = Clean(args[1]);
}
else
{
    Console.Write("Enter your Google Client ID: ");
    clientId = Clean(Console.ReadLine());

    Console.Write("Enter your Google Client Secret: ");
    clientSecret = Clean(Console.ReadLine());
}

// Strip whitespace and any BOM picked up from piped/pasted input
static string Clean(string value) => value?.Trim().Trim('\uFEFF');

if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
{
    Console.WriteLine("Error: Client ID and Secret are required.");
    return;
}

Console.WriteLine();
Console.WriteLine("Opening browser for Google authorization...");
Console.WriteLine("Grant access to Gmail (read/modify — needed to mark emails read and apply labels) and Google Calendar.");
Console.WriteLine();

var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    new ClientSecrets
    {
        ClientId = clientId,
        ClientSecret = clientSecret
    },
    new[]
    {
        GmailService.Scope.GmailModify,
        CalendarService.Scope.Calendar
    },
    "user",
    CancellationToken.None,
    new FileDataStore("Alfred.GoogleAuth", fullPath: false),
    new UrlPrintingCodeReceiver());

Console.WriteLine("Authorization successful!");
Console.WriteLine();
Console.WriteLine("Add this to your local.settings.json:");
Console.WriteLine();
Console.WriteLine($"  \"Google:ClientId\": \"{clientId}\",");
Console.WriteLine($"  \"Google:ClientSecret\": \"{clientSecret}\",");
Console.WriteLine($"  \"Google:RefreshToken\": \"{credential.Token.RefreshToken}\"");
Console.WriteLine();
Console.WriteLine("Done! You can close this window.");

// Prints the authorization URL before delegating to the standard localhost receiver,
// so the URL can be opened manually if the automatic browser launch fails or mangles it
class UrlPrintingCodeReceiver : ICodeReceiver
{
    private readonly LocalServerCodeReceiver _inner = new();

    public string RedirectUri => _inner.RedirectUri;

    public Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(
        AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("If no browser opens (or it shows an error), open this URL manually:");
        Console.WriteLine(url.Build().AbsoluteUri);
        Console.WriteLine();
        return _inner.ReceiveCodeAsync(url, taskCancellationToken);
    }
}
