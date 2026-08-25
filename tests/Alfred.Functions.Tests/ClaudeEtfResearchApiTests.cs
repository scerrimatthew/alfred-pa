using System.Text.Json;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Tests.Support;
using Anthropic.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Drives ClaudeEtfResearchService end to end through the ClientFactory seam: the real
// Anthropic SDK serializes the request and parses canned API JSON, so these pin the
// whole flow — model + web-search budget out, report back — plus the shared
// WebResearchRunner behavior (pause_turn resume, resume cap, budget cut-off).
public class ClaudeEtfResearchApiTests
{
    private readonly FakeHttpHandler _http = new();
    private readonly ClaudeEtfResearchService _service;

    public ClaudeEtfResearchApiTests()
    {
        _service = new ClaudeEtfResearchService(NullLogger<ClaudeEtfResearchService>.Instance)
        {
            ClientFactory = () => new AnthropicClient("test-key", new HttpClient(_http))
        };
    }

    private static string EtfResponse(string stopReason, params string[] texts) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-test",
            content = texts.Select(object (t) => new { type = "text", text = t }).ToArray(),
            stop_reason = stopReason,
            usage = new { input_tokens = 10, output_tokens = 20 }
        });

    private const string ReportJson =
        """{"items": [{"symbol": "VWCE", "quote": "€128.42", "weekChangePercent": -1.4, "narrative": "Slipped with the dollar."}], "telegramMessage": "📈 Week of 10-14 Aug"}""";

    // A paused search turn as the API sends it — the server_tool_use /
    // web_search_tool_result blocks must travel back verbatim to resume the turn
    private const string PausedSearchResponse =
        """
        {
          "id": "msg_1",
          "type": "message",
          "role": "assistant",
          "model": "claude-test",
          "content": [
            {"type": "server_tool_use", "id": "srvtoolu_01", "name": "web_search", "input": {"query": "VWCE weekly close"}},
            {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_01", "content": [
              {"type": "web_search_result", "url": "https://example.com", "title": "Example", "encrypted_content": "ENC123", "page_age": "1 day ago"}
            ]},
            {"type": "text", "text": "Looking up this week's closes..."}
          ],
          "stop_reason": "pause_turn",
          "usage": {"input_tokens": 10, "output_tokens": 20}
        }
        """;

    private JsonElement RequestBody(int index = 0) =>
        JsonDocument.Parse(_http.Requests[index].Body!).RootElement;

    [Fact]
    public async Task NothingTracked_ReturnsAnEmptyReportWithoutCallingTheApi()
    {
        var report = await _service.ResearchWeeklyPerformanceAsync([]);

        Assert.Empty(_http.Requests); // no watchlist, no spend
        Assert.Empty(report.Items);
        Assert.Null(report.TelegramMessage);
        Assert.False(report.Incomplete);
    }

    [Fact]
    public async Task HappyPath_SendsTheResearchRequestAndParsesTheLastTextBlock()
    {
        _http.EnqueueJson(EtfResponse("end_turn",
            "Let me check the week's closes...", // search narration must be ignored
            ReportJson));

        var report = await _service.ResearchWeeklyPerformanceAsync([
            EtfHolding("VWCE", name: "Vanguard FTSE All-World UCITS ETF", notes: "core holding")
        ]);

        Assert.Equal("📈 Week of 10-14 Aug", report.TelegramMessage);
        Assert.False(report.Incomplete);
        var item = Assert.Single(report.Items);
        Assert.Equal("VWCE", item.Symbol);
        Assert.Equal(-1.4, item.WeekChangePercent);

        var request = _http.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri.GetLeftPart(UriPartial.Path));
        var body = RequestBody();
        // Sonnet per the cost policy: a factual price read, not an Opus-grade judgment task
        Assert.Equal("claude-sonnet-5", body.GetProperty("model").GetString());
        Assert.Equal(16000, body.GetProperty("max_tokens").GetInt32());
        // One fund: the base budget plus one search for it
        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.Equal(5, tool.GetProperty("max_uses").GetInt32());
        // The watchlist itself rides in the user turn, the rules in the system prompt
        Assert.Contains("- VWCE — Vanguard FTSE All-World UCITS ETF", body.GetProperty("messages").ToString());
        Assert.Contains("Never give buy, sell, or hold advice", body.GetProperty("system").ToString());
        // ...with the shared runner's cache breakpoint: every pause_turn resume re-sends
        // the whole prefix, so the system prompt must be marked cacheable
        Assert.Equal("ephemeral",
            body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task SearchBudget_GrowsWithTheWatchlistUpToTheCeiling()
    {
        _http.Route("POST /v1/messages", EtfResponse("end_turn", ReportJson));

        await _service.ResearchWeeklyPerformanceAsync([EtfHolding("A"), EtfHolding("B"), EtfHolding("C")]);
        Assert.Equal(7, RequestBody().GetProperty("tools")[0].GetProperty("max_uses").GetInt32());

        await _service.ResearchWeeklyPerformanceAsync(
            Enumerable.Range(1, 10).Select(i => EtfHolding($"ETF{i}")).ToList());
        // Never past the ceiling the news digest also uses
        Assert.Equal(12, RequestBody(1).GetProperty("tools")[0].GetProperty("max_uses").GetInt32());
    }

    [Fact]
    public async Task OnDemand_ShiftsTheWindowFramingInTheSystemPrompt()
    {
        _http.EnqueueJson(EtfResponse("end_turn", ReportJson));

        await _service.ResearchWeeklyPerformanceAsync([EtfHolding("VWCE")], onDemand: true);

        var system = RequestBody().GetProperty("system").ToString();
        Assert.Contains("Matthew asked for this now", system);
        Assert.DoesNotContain("Monday to Friday close", system);
    }

    [Fact]
    public async Task PauseTurn_ResendsThePartialTurnAndCompletesOnTheSecondReply()
    {
        _http.EnqueueJson(PausedSearchResponse);
        _http.EnqueueJson(EtfResponse("end_turn", ReportJson));

        var report = await _service.ResearchWeeklyPerformanceAsync([EtfHolding("VWCE")]);

        Assert.Equal("📈 Week of 10-14 Aug", report.TelegramMessage);
        Assert.Single(report.Items);

        Assert.Equal(2, _http.Requests.Count);
        var resumeMessages = RequestBody(1).GetProperty("messages");
        Assert.Equal(2, resumeMessages.GetArrayLength());
        Assert.Equal("assistant", resumeMessages[1].GetProperty("role").GetString());
        // The search blocks must survive verbatim or the server restarts the research
        var resumedTurn = resumeMessages[1].ToString();
        Assert.Contains("server_tool_use", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("srvtoolu_01", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("ENC123", resumedTurn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseTurn_ThatNeverCompletes_GivesUpAfterFiveResumes_FlaggedIncomplete()
    {
        _http.Route("POST /v1/messages", PausedSearchResponse);

        var report = await _service.ResearchWeeklyPerformanceAsync([EtfHolding("VWCE")]);

        Assert.Equal(6, _http.Requests.Count); // the initial call + 5 resumes
        // A give-up must be told apart from "no numbers found" — the caller words them differently
        Assert.True(report.Incomplete);
        Assert.Empty(report.Items);
        Assert.Null(report.TelegramMessage);
    }

    [Fact]
    public async Task BudgetCancellation_YieldsAnIncompleteReportInsteadOfThrowing()
    {
        _http.RouteResponder("POST /v1/messages", _ => throw new TaskCanceledException("simulated budget cut-off"));

        var report = await _service.ResearchWeeklyPerformanceAsync([EtfHolding("VWCE")]);

        Assert.True(report.Incomplete);
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task ReplyWithoutAnyTextBlock_YieldsAnEmptyButCompleteReport()
    {
        _http.EnqueueJson(EtfResponse("end_turn")); // no content blocks at all

        var report = await _service.ResearchWeeklyPerformanceAsync([EtfHolding("VWCE")]);

        Assert.Empty(report.Items);
        Assert.Null(report.TelegramMessage);
        Assert.False(report.Incomplete);
    }

    [Fact]
    public void SharedBudget_StillGovernsBothResearchRuns()
    {
        // The ETF report reuses the news runner's wall-clock budget; it must keep leaving
        // margin before Azure's 10-minute hard kill, where no catch block gets to apologize
        Assert.Equal(WebResearchRunner.Budget, ClaudeNewsResearchService.ResearchBudget);
        Assert.True(WebResearchRunner.Budget <= TimeSpan.FromMinutes(9),
            "the shared web-research budget must leave at least a minute of margin before Azure's functionTimeout");
        // ...and the shared client's ceiling must sit above the budget, so the token cuts
        // the run off rather than an HttpClient timeout surfacing as a hard failure
        Assert.True(WebResearchRunner.LongRunHttpClient.Timeout > WebResearchRunner.Budget,
            "the research HttpClient must not time out before the run's own budget does");
    }
}
