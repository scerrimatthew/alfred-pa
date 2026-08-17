namespace Alfred.Functions.Services.Gmail;

public static class GmailLinks
{
    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("Alfred__PublicBaseUrl")?.TrimEnd('/')
        ?? "https://func-matt-scerri-alfred-prod-westeu-001.azurewebsites.net";

    // Points at GmailRedirectFunction, which bounces to the native Gmail app scheme
    // (Telegram links can't invoke custom schemes directly) with a web fallback.
    public static string ForThread(string threadId) => $"{BaseUrl}/api/open/{threadId}";
}
