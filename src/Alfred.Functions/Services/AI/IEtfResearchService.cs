using Alfred.Functions.Models;

namespace Alfred.Functions.Services.AI;

public interface IEtfResearchService
{
    // Researches how each ETF on the watchlist did over the past week via web search and
    // writes a short narrative per fund explaining what moved it. The holdings carry last
    // week's snapshot so the narrative can talk about continuation or reversal.
    // onDemand shifts the framing for a mid-week "/etf" request.
    Task<EtfReport> ResearchWeeklyPerformanceAsync(List<EtfHoldingEntity> holdings, bool onDemand = false);
}
