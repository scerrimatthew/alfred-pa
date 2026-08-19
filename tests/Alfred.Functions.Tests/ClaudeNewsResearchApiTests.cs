using System.Text.Json;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Tests.Support;
using Anthropic.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Drives ClaudeNewsResearchService end to end through the ClientFactory seam: the real
// Anthropic SDK serializes the request and parses canned API JSON, so these tests pin
// the whole flow — model + web-search tool out, digest back — plus the pause_turn
// resume loop that server-side web search introduces.
public class ClaudeNewsResearchApiTests
{
    private readonly FakeHttpHandler _http = new();
    private readonly ClaudeNewsResearchService _service;

    public ClaudeNewsResearchApiTests()
    {
        _service = new ClaudeNewsResearchService(Options(), NullLogger<ClaudeNewsResearchService>.Instance)
        {
            ClientFactory = () => new AnthropicClient("test-key", new HttpClient(_http))
        };
    }

    // Canned Messages-API reply whose content is a sequence of text blocks — with server
    // tools the narration blocks come first and the answer is the last one
    private static string NewsResponse(string stopReason, params string[] texts) =>
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

    private const string DigestJson =
        """{"items": [{"headline": "DORA 2026 lands", "url": "https://dora.dev/2026", "category": "thesis-evidence"}], "telegramMessage": "🗞 Evening!"}""";

    // A paused search turn as the API actually sends it: narration text plus the
    // server_tool_use / web_search_tool_result blocks (with encrypted_content) that the
    // API needs back VERBATIM to resume the turn instead of restarting the research
    private const string PausedSearchResponse =
        """
        {
          "id": "msg_1",
          "type": "message",
          "role": "assistant",
          "model": "claude-test",
          "content": [
            {"type": "server_tool_use", "id": "srvtoolu_01", "name": "web_search", "input": {"query": "ai news"}},
            {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_01", "content": [
              {"type": "web_search_result", "url": "https://example.com", "title": "Example", "encrypted_content": "ENC123", "page_age": "1 day ago"}
            ]},
            {"type": "text", "text": "Searching the web for AI news..."}
          ],
          "stop_reason": "pause_turn",
          "usage": {"input_tokens": 10, "output_tokens": 20}
        }
        """;

    private JsonElement RequestBody(int index = 0) =>
        JsonDocument.Parse(_http.Requests[index].Body!).RootElement;

    [Fact]
    public async Task HappyPath_SendsTheResearchRequestAndParsesTheLastTextBlock()
    {
        _http.EnqueueJson(NewsResponse("end_turn",
            "Let me look at today's AI news...", // search narration must be ignored
            DigestJson));

        var digest = await _service.ResearchDailyNewsAsync([], []);

        Assert.Equal("🗞 Evening!", digest.TelegramMessage);
        var item = Assert.Single(digest.Items);
        Assert.Equal("DORA 2026 lands", item.Headline);
        Assert.Equal("https://dora.dev/2026", item.Url);
        Assert.Equal("thesis-evidence", item.Category);

        var request = _http.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri.GetLeftPart(UriPartial.Path));
        var body = RequestBody();
        Assert.Equal("claude-opus-5", body.GetProperty("model").GetString());
        Assert.Equal(16000, body.GetProperty("max_tokens").GetInt32());
        // The server-side web-search tool must travel with the request
        Assert.Contains("web_search", request.Body, StringComparison.Ordinal);
        Assert.Equal(1, body.GetProperty("tools").GetArrayLength());
        // The watchlist brief rides in the system prompt
        Assert.Contains("WATCHLIST", body.GetProperty("system").ToString());
    }

    [Fact]
    public async Task RulesAndCoveredStories_TravelWithTheRequest()
    {
        _http.EnqueueJson(NewsResponse("end_turn", DigestJson));

        var rules = new List<NewsRuleEntity>
        {
            new() { RowKey = "n1", Instruction = "Skip funding rounds", CreatedAt = DateTimeOffset.UtcNow }
        };
        var covered = new List<ReportedNewsEntity>
        {
            new() { Headline = "Old story", Url = "https://old.example/1", ReportedAt = DateTimeOffset.UtcNow.AddDays(-2) }
        };

        await _service.ResearchDailyNewsAsync(rules, covered);

        var body = RequestBody();
        Assert.Contains("[n1] Skip funding rounds", body.GetProperty("system").ToString());
        Assert.Contains("Old story (https://old.example/1)", body.GetProperty("messages").ToString());
    }

    [Fact]
    public async Task PauseTurn_ResendsThePartialTurnAndCompletesOnTheSecondReply()
    {
        _http.EnqueueJson(PausedSearchResponse);
        _http.EnqueueJson(NewsResponse("end_turn", DigestJson));

        var digest = await _service.ResearchDailyNewsAsync([], []);

        Assert.Equal("🗞 Evening!", digest.TelegramMessage);
        Assert.Single(digest.Items);

        // The resume request must append the paused assistant turn so the server
        // can pick up where it left off
        Assert.Equal(2, _http.Requests.Count);
        var resumeMessages = RequestBody(1).GetProperty("messages");
        Assert.Equal(2, resumeMessages.GetArrayLength());
        Assert.Equal("assistant", resumeMessages[1].GetProperty("role").GetString());

        // ...and it must carry the FULL block set, not just the narration text:
        // the API resumes a search turn from the server_tool_use /
        // web_search_tool_result blocks, and encrypted_content must survive the
        // round-trip verbatim (response.Message strips these — the fixed code
        // resends response.Content instead)
        var resumedTurn = resumeMessages[1].ToString();
        Assert.Contains("Searching the web for AI news...", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("server_tool_use", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("srvtoolu_01", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("ai news", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("web_search_tool_result", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("ENC123", resumedTurn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseTurn_ThatNeverCompletes_GivesUpAfterFiveResumes()
    {
        _http.Route("POST /v1/messages", PausedSearchResponse);

        var digest = await _service.ResearchDailyNewsAsync([], []);

        Assert.Equal(6, _http.Requests.Count); // the initial call + 5 resumes
        Assert.Empty(digest.Items);
        Assert.Null(digest.TelegramMessage);

        // Every resume accumulates another full paused turn: by the last request all
        // five assistant turns ride along with their search blocks intact
        var finalMessages = RequestBody(5).GetProperty("messages");
        Assert.Equal(6, finalMessages.GetArrayLength());
        Assert.Contains("ENC123", finalMessages[5].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyWithoutAnyTextBlock_YieldsAnEmptyDigest()
    {
        _http.EnqueueJson(NewsResponse("end_turn")); // no content blocks at all

        var digest = await _service.ResearchDailyNewsAsync([], []);

        Assert.Empty(digest.Items);
        Assert.Null(digest.TelegramMessage);
    }
}
