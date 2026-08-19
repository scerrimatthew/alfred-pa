namespace Alfred.Functions.Models;

// Result of one AI-news research run: the formatted Telegram briefing plus the
// individual stories it covered (recorded for dedup against future runs)
public class AiNewsDigest
{
    public string? TelegramMessage { get; set; }
    public List<AiNewsItem> Items { get; set; } = [];

    // True when the research run was cut off (wall-clock budget spent, or the server kept
    // pausing past the resume cap) — an empty digest then means "couldn't finish", which
    // callers must report differently from a genuinely quiet news day
    public bool Incomplete { get; set; }
}

public class AiNewsItem
{
    public string Headline { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Category { get; set; }
    // Stored alongside the headline so chat follow-ups and the weekly synthesis can
    // work from what was actually reported, not just a title
    public string? Summary { get; set; }
    public string? WhyItMatters { get; set; }
}
