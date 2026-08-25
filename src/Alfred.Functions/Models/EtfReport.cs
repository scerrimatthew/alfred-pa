namespace Alfred.Functions.Models;

// Result of one weekly ETF research run: the formatted Telegram briefing plus the
// per-ETF numbers (stored back on the watchlist so next week can compare)
public class EtfReport
{
    public string? TelegramMessage { get; set; }
    public List<EtfPerformance> Items { get; set; } = [];

    // True when the research run was cut off (wall-clock budget spent, or the server kept
    // pausing past the resume cap) — an empty report then means "couldn't finish", which
    // callers must report differently from "no numbers found"
    public bool Incomplete { get; set; }
}

public class EtfPerformance
{
    public string Symbol { get; set; } = string.Empty;
    public string? Name { get; set; }
    // Latest close as Claude found it, currency included ("€128.42")
    public string? Quote { get; set; }
    public double? WeekChangePercent { get; set; }
    public double? YtdChangePercent { get; set; }
    // The point of the whole feature: a couple of sentences on what moved it and why
    public string? Narrative { get; set; }
    public string? SourceUrl { get; set; }
}
