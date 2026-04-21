namespace Alfred.Functions.Configuration;

public class AlfredOptions
{
    public const string SectionName = "Alfred";

    public string SchoolEmailSender { get; set; } = "noreply@myschoolmanagement.com";
    public string SharedCalendarId { get; set; } = string.Empty;
    public string TelegramChatId { get; set; } = string.Empty;
    public bool SendEmptyDigest { get; set; } = false;
    public int LookbackHours { get; set; } = 25;
    public int SchoolDaysAhead { get; set; } = 5;
}

public class GoogleOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
