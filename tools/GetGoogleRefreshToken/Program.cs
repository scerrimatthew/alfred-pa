using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;

Console.WriteLine("=== Alfred — Google OAuth2 Refresh Token Setup ===");
Console.WriteLine();

Console.Write("Enter your Google Client ID: ");
var clientId = Console.ReadLine()?.Trim();

Console.Write("Enter your Google Client Secret: ");
var clientSecret = Console.ReadLine()?.Trim();

if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
{
    Console.WriteLine("Error: Client ID and Secret are required.");
    return;
}

Console.WriteLine();
Console.WriteLine("Opening browser for Google authorization...");
Console.WriteLine("Grant access to Gmail (read-only) and Google Calendar.");
Console.WriteLine();

var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    new ClientSecrets
    {
        ClientId = clientId,
        ClientSecret = clientSecret
    },
    new[]
    {
        GmailService.Scope.GmailReadonly,
        CalendarService.Scope.Calendar
    },
    "user",
    CancellationToken.None,
    new FileDataStore("Alfred.GoogleAuth", fullPath: false));

Console.WriteLine("Authorization successful!");
Console.WriteLine();
Console.WriteLine("Add this to your local.settings.json:");
Console.WriteLine();
Console.WriteLine($"  \"Google:ClientId\": \"{clientId}\",");
Console.WriteLine($"  \"Google:ClientSecret\": \"{clientSecret}\",");
Console.WriteLine($"  \"Google:RefreshToken\": \"{credential.Token.RefreshToken}\"");
Console.WriteLine();
Console.WriteLine("Done! You can close this window.");
