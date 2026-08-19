namespace Alfred.Functions.Models;

// Result of one AI-news research run: the formatted Telegram briefing plus the
// individual stories it covered (recorded for dedup against future runs)
public class AiNewsDigest
{
    public string? TelegramMessage { get; set; }
    public List<AiNewsItem> Items { get; set; } = [];
}

public class AiNewsItem
{
    public string Headline { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Category { get; set; }
}
