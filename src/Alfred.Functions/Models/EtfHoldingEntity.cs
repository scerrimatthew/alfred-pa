using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// One ETF Matthew follows. Added from chat ("track VWCE") or seeded from the
// Alfred__EtfTickers app setting; the weekly report walks this list.
public class EtfHoldingEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "etfs";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Ticker as Matthew says it ("VWCE", "SXR8.DE"); RowKey is its normalized form
    public string Symbol { get; set; } = string.Empty;
    // Full fund name when known ("Vanguard FTSE All-World UCITS ETF")
    public string? Name { get; set; }
    // Why he holds/watches it ("core holding, monthly DCA") — colours the narrative
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Last reported snapshot, fed back into the next report so the narrative can talk
    // about continuation or reversal instead of describing each week from scratch
    public string? LastQuote { get; set; }
    public double? LastWeekChangePercent { get; set; }
    public DateTimeOffset? LastReportedAt { get; set; }
}

// One-shot marker recording that Alfred has already asked which ETFs to follow, so the
// weekly report nudges Matthew once when nothing is tracked instead of every Saturday.
// Lives in the EtfHoldings table under its own partition, invisible to the watchlist query.
public class EtfNudgeEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "meta";
    public string RowKey { get; set; } = "onboarding-nudge";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset SentAt { get; set; }
}
