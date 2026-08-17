namespace Alfred.Functions.Services.Gmail;

public static class GmailLinks
{
    // Universal link — opens the Gmail app on iOS/Android when installed, web otherwise.
    // #all works regardless of whether the thread is archived out of the inbox.
    public static string ForThread(string threadId) => $"https://mail.google.com/mail/u/0/#all/{threadId}";
}
