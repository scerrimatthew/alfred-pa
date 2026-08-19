using System.Text.Json;
using System.Text.Json.Nodes;
using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Alfred.Functions.Tests.Support;
using Anthropic.SDK;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Drives ClaudeSummarizerService end to end through the ClientFactory seam: the real
// Anthropic SDK serializes the request and parses canned API JSON, so these tests pin
// the whole flow — prompt in, JSON out, parsed model back — plus the tool-use loop.
public class ClaudeSummarizerApiTests
{
    private readonly FakeHttpHandler _http = new();
    private readonly ClaudeSummarizerService _service;

    public ClaudeSummarizerApiTests()
    {
        _service = new ClaudeSummarizerService(NullLogger<ClaudeSummarizerService>.Instance)
        {
            ClientFactory = () => new AnthropicClient("test-key", new HttpClient(_http))
        };
    }

    private static string TextResponse(string text) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-test",
            content = new object[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 20 }
        });

    private static string ToolUseResponse(string toolName, object input, string id = "tu1") =>
        JsonSerializer.Serialize(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-test",
            content = new object[] { new { type = "tool_use", id, name = toolName, input } },
            stop_reason = "tool_use",
            usage = new { input_tokens = 10, output_tokens = 20 }
        });

    private JsonElement RequestBody(int index = 0) =>
        JsonDocument.Parse(_http.Requests[index].Body!).RootElement;

    private string RequestUserText(int requestIndex = 0, int messageIndex = 0)
    {
        var content = RequestBody(requestIndex).GetProperty("messages")[messageIndex].GetProperty("content");
        // The SDK may send content as a plain string or as a block array
        return content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray()
                .Where(b => b.TryGetProperty("text", out _))
                .Select(b => b.GetProperty("text").GetString()));
    }

    // ---- School summarization ----

    [Fact]
    public async Task SummarizeEmail_SendsThePromptAndParsesTheDigestReply()
    {
        _http.EnqueueJson(TextResponse("""
            {"telegramMessage": "📩 <b>ZOO OUTING</b>", "calendarEvents": [
                {"title": "Outing: Zoo Year 1", "description": "Hat", "date": "2026-09-10", "action": "create"}
            ], "homework": null, "requiresImmediateAlert": false, "category": "outing"}
            """));

        var email = Email(subject: "Zoo outing", body: "We are going to the zoo");
        email.Documents.Add(new LinkedDocument
        {
            Title = "permission.pdf",
            Url = "attachment:permission.pdf",
            Source = LinkedDocumentSource.EmailAttachment,
            ExtractedText = "Please sign the permission slip"
        });

        var digest = await _service.SummarizeEmailAsync(email);

        Assert.Equal("📩 <b>ZOO OUTING</b>", digest.TelegramMessage);
        Assert.Equal("outing", digest.Category);
        Assert.Equal("Outing: Zoo Year 1", Assert.Single(digest.CalendarEvents).Title);

        var request = _http.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri.GetLeftPart(UriPartial.Path));
        var body = RequestBody();
        Assert.Equal(Anthropic.SDK.Constants.AnthropicModels.Claude45Sonnet, body.GetProperty("model").GetString());
        Assert.Equal(8192, body.GetProperty("max_tokens").GetInt32());

        var prompt = RequestUserText();
        Assert.Contains("Zoo outing", prompt);
        Assert.Contains("We are going to the zoo", prompt);
        Assert.Contains("Please sign the permission slip", prompt); // attachment text reaches the prompt
    }

    [Fact]
    public async Task TriagePersonalEmail_CarriesRulesIntoThePromptAndParsesTheVerdict()
    {
        _http.EnqueueJson(TextResponse("""
            {"suppressed": false, "requiresAttention": true, "category": "invoice",
             "summary": "GO bill, €45.20 due 25 Aug.", "telegramMessage": "GO bill in — <b>€45.20</b>.",
             "calendarEvents": [], "fraudWarning": null, "needsReply": false}
            """));

        var rules = new List<SuppressionRuleEntity> { new() { RowKey = "r1", Pattern = "Monthly Bolt reports" } };
        var triage = await _service.TriagePersonalEmailAsync(Email(subject: "GO bill"), rules, [], []);

        Assert.True(triage.RequiresAttention);
        Assert.Equal("invoice", triage.Category);
        Assert.Equal("GO bill in — <b>€45.20</b>.", triage.TelegramMessage);

        var prompt = RequestUserText();
        Assert.Contains("GO bill", prompt);
        Assert.Contains("[r1] Monthly Bolt reports", prompt); // suppression rules travel with every triage
        Assert.Equal(2048, RequestBody().GetProperty("max_tokens").GetInt32());
    }

    // ---- Digests and Q&A ----

    [Fact]
    public async Task BuildEveningDigest_FeedsEmailsEventsAndHomeworkToTheModel()
    {
        _http.EnqueueJson(TextResponse("THE DIGEST"));

        var emails = new List<ProcessedEmailEntity>
        {
            ProcessedEmail(subject: "Weekly plan", senderName: "Teacher", summary: "PE kit on Monday")
        };
        emails[0].Homework = "Read pages 1-3";
        var events = new List<Event>
        {
            new() { Summary = "Sports Day", Description = "Wear kit", Start = new EventDateTime { Date = "2026-09-10" } }
        };

        var digest = await _service.BuildEveningDigestAsync(emails, events);

        Assert.Equal("THE DIGEST", digest);
        var prompt = RequestUserText();
        Assert.Contains("[Teacher] Weekly plan: PE kit on Monday", prompt);
        Assert.Contains("Sports Day", prompt);
        Assert.Contains("Read pages 1-3", prompt);
        // The persona and structure ride in the system prompt
        var system = RequestBody().GetProperty("system");
        Assert.Contains("Alfred", system.ToString());
    }

    [Fact]
    public async Task BuildPersonalDigest_IncludesTheAwaitingReplyNudges()
    {
        _http.EnqueueJson(TextResponse("🤖 Evening!"));

        var awaiting = new List<ProcessedEmailEntity>
        {
            ProcessedEmail(subject: "Weekend plans", senderName: "Sarah", summary: "Asked about Saturday.")
        };

        var digest = await _service.BuildPersonalDigestAsync([], [], awaiting);

        Assert.Equal("🤖 Evening!", digest);
        var prompt = RequestUserText();
        Assert.Contains("Sarah — Weekend plans", prompt);
        Assert.Contains("No personal emails today.", prompt);
        Assert.Contains("No upcoming actions.", prompt);
    }

    [Fact]
    public async Task AnswerQuestion_ReplaysRecentConversationForFollowUps()
    {
        _http.EnqueueJson(TextResponse("PE kit tomorrow."));

        var turns = new List<ChatTurnEntity>
        {
            new() { Question = "what's on Monday?", Answer = "Swimming.", AskedAt = DateTimeOffset.UtcNow.AddMinutes(-10) }
        };

        var answer = await _service.AnswerQuestionAsync("and Tuesday?", [], [], turns);

        Assert.Equal("PE kit tomorrow.", answer);
        var prompt = RequestUserText();
        Assert.Contains("RECENT CONVERSATION", prompt);
        Assert.Contains("Q: what's on Monday?", prompt);
        Assert.Contains("and Tuesday?", prompt);
    }

    // ---- Jokes ----

    [Fact]
    public async Task TellJoke_SendsTheButlerPromptAndReturnsTheTrimmedReply()
    {
        _http.EnqueueJson(TextResponse("  Why did the scarecrow win an award? <b>Outstanding in his field.</b>\n"));

        var joke = await _service.TellJokeAsync("", []);

        Assert.Equal("Why did the scarecrow win an award? <b>Outstanding in his field.</b>", joke);

        var body = RequestBody();
        Assert.Equal(Anthropic.SDK.Constants.AnthropicModels.Claude46Opus, body.GetProperty("model").GetString());
        Assert.Equal(512, body.GetProperty("max_tokens").GetInt32());
        // The contract: one short, clean joke, no preamble. Read the system text with
        // GetString() — the SDK unicode-escapes angle brackets and ampersands on the
        // wire, so the raw JSON text would hide exactly the characters the rule is about
        var system = body.GetProperty("system")[0].GetProperty("text").GetString()!;
        Assert.Contains("exactly ONE joke", system);
        Assert.Contains("family-friendly", system);
        Assert.Contains("the joke only", system);
        // HTML-mode safety: an "R&D" or "x < y" joke with raw <, > or & fails to send
        Assert.Contains("never use raw <, > or & characters", system);
        Assert.Contains("&lt; &gt; &amp;", system);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TellJoke_NoTopic_TellsTheModelToPickOne(string topic)
    {
        _http.EnqueueJson(TextResponse("A joke."));

        await _service.TellJokeAsync(topic, []);

        var prompt = RequestUserText();
        Assert.Contains("No topic was given — pick something yourself.", prompt);
        Assert.DoesNotContain("Topic requested:", prompt);
    }

    [Fact]
    public async Task TellJoke_Topic_IsPassedThroughToThePrompt()
    {
        _http.EnqueueJson(TextResponse("A penguin joke."));

        await _service.TellJokeAsync("penguins", []);

        var prompt = RequestUserText();
        Assert.Contains("Topic requested: penguins", prompt);
        Assert.DoesNotContain("No topic was given", prompt);
    }

    [Fact]
    public async Task TellJoke_RecentJokes_RideAlongAsADoNotRepeatList()
    {
        _http.EnqueueJson(TextResponse("A fresh joke."));

        await _service.TellJokeAsync("", ["Old joke A", "Old joke B"]);

        var prompt = RequestUserText();
        Assert.Contains("JOKES YOU ALREADY TOLD IN THIS CHAT", prompt);
        Assert.Contains("- Old joke A", prompt);
        Assert.Contains("- Old joke B", prompt);
    }

    [Fact]
    public async Task TellJoke_NoRecentJokes_OmitsTheDoNotRepeatSection()
    {
        _http.EnqueueJson(TextResponse("A joke."));

        await _service.TellJokeAsync("", []);

        Assert.DoesNotContain("JOKES YOU ALREADY TOLD", RequestUserText());
    }

    [Fact]
    public async Task TellJoke_ReplyWithoutText_FallsBackToAnApology()
    {
        // A reply with no text blocks at all
        _http.EnqueueJson(JsonSerializer.Serialize(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-test",
            content = Array.Empty<object>(),
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 20 }
        }));

        var joke = await _service.TellJokeAsync("", []);

        Assert.Equal("I'm afraid my sense of humour has failed me. Ask me again in a moment.", joke);
    }

    // ---- The personal Q&A tool loop ----

    [Fact]
    public async Task AnswerPersonalQuestion_NoToolsNeeded_ReturnsTheTextDirectly()
    {
        _http.EnqueueJson(TextResponse("Nothing is due."));

        var answer = await _service.AnswerPersonalQuestionAsync(
            "anything due?", [], [], [], [], [], [],
            (_, _) => Task.FromResult("should never be called"));

        Assert.Equal("Nothing is due.", answer);
        // The tool definitions must travel with the request — including the server-side
        // web-search tool for news follow-ups
        var body = RequestBody();
        var toolNames = body.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("search_inbox", toolNames);
        Assert.Contains("draft_reply", toolNames);
        Assert.Contains("create_calendar_event", toolNames);
        Assert.Contains("web_search", toolNames);
        // Web-search turns carry narration on top of the answer — the budget must cover it
        Assert.Equal(4096, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task AnswerPersonalQuestion_RecentNewsRidesInThePrompt()
    {
        _http.EnqueueJson(TextResponse("It was the DORA study."));

        var recentNews = new List<ReportedNewsEntity>
        {
            new()
            {
                Headline = "DORA 2026 lands", Url = "https://dora.dev/2026", Category = "thesis-evidence",
                Summary = "Review times doubled.", WhyItMatters = "Core evidence.",
                ReportedAt = new DateTimeOffset(2026, 8, 17, 18, 0, 0, TimeSpan.Zero)
            }
        };

        await _service.AnswerPersonalQuestionAsync(
            "what was that DORA story?", [], [], [], [], recentNews, [],
            (_, _) => Task.FromResult(""));

        var prompt = RequestUserText();
        Assert.Contains("RECENT AI NEWS", prompt);
        Assert.Contains("[thesis-evidence] DORA 2026 lands (https://dora.dev/2026): Review times doubled. | why it mattered: Core evidence.", prompt);
    }

    [Fact]
    public async Task AnswerPersonalQuestion_NoRecentNews_SaysSoInThePrompt()
    {
        _http.EnqueueJson(TextResponse("ok"));

        await _service.AnswerPersonalQuestionAsync("q", [], [], [], [], [], [],
            (_, _) => Task.FromResult(""));

        Assert.Contains("No AI news reported recently.", RequestUserText());
    }

    [Fact]
    public async Task AnswerPersonalQuestion_TheAnswerIsTheLastTextBlock()
    {
        // With server tools in play, earlier text blocks are search narration
        _http.EnqueueJson(JsonSerializer.Serialize(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-test",
            content = new object[]
            {
                new { type = "text", text = "Let me check that story..." },
                new { type = "text", text = "Here's the read-out." }
            },
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 20 }
        }));

        var answer = await _service.AnswerPersonalQuestionAsync(
            "tell me more", [], [], [], [], [], [],
            (_, _) => Task.FromResult(""));

        Assert.Equal("Here's the read-out.", answer);
    }

    [Fact]
    public async Task AnswerPersonalQuestion_PauseTurn_ResumesWithTheFullPartialTurn()
    {
        // A server web-search turn pausing mid-run, exactly as the API sends it
        _http.EnqueueJson("""
            {
              "id": "msg_1",
              "type": "message",
              "role": "assistant",
              "model": "claude-test",
              "content": [
                {"type": "server_tool_use", "id": "srvtoolu_01", "name": "web_search", "input": {"query": "dora study"}},
                {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_01", "content": [
                  {"type": "web_search_result", "url": "https://example.com", "title": "Example", "encrypted_content": "ENC123", "page_age": "1 day ago"}
                ]},
                {"type": "text", "text": "Searching..."}
              ],
              "stop_reason": "pause_turn",
              "usage": {"input_tokens": 10, "output_tokens": 20}
            }
            """);
        _http.EnqueueJson(TextResponse("Here's what I found."));

        var answer = await _service.AnswerPersonalQuestionAsync(
            "look up the DORA study", [], [], [], [], [], [],
            (_, _) => Task.FromResult("client tools must not run for a paused search"));

        Assert.Equal("Here's what I found.", answer);
        Assert.Equal(2, _http.Requests.Count);

        // The resume must append the FULL paused turn — server_tool_use and
        // web_search_tool_result blocks included — so the server resumes rather
        // than restarting the search
        var resumeMessages = JsonDocument.Parse(_http.Requests[1].Body!).RootElement.GetProperty("messages");
        Assert.Equal(2, resumeMessages.GetArrayLength());
        Assert.Equal("assistant", resumeMessages[1].GetProperty("role").GetString());
        var resumedTurn = resumeMessages[1].ToString();
        Assert.Contains("server_tool_use", resumedTurn, StringComparison.Ordinal);
        Assert.Contains("ENC123", resumedTurn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerPersonalQuestion_ExecutesTheRequestedToolAndFeedsTheResultBack()
    {
        _http.EnqueueJson(ToolUseResponse("list_snoozes", new { }));
        _http.EnqueueJson(TextResponse("You have no reminders pending."));

        var executed = new List<(string Tool, JsonNode? Input)>();
        var answer = await _service.AnswerPersonalQuestionAsync(
            "what have I snoozed?", [], [], [], [], [], [],
            (tool, input) =>
            {
                executed.Add((tool, input));
                return Task.FromResult("No reminders are pending.");
            });

        Assert.Equal("You have no reminders pending.", answer);
        Assert.Equal("list_snoozes", Assert.Single(executed).Tool);

        // The second request must carry the tool result back, correlated by tool_use id
        Assert.Equal(2, _http.Requests.Count);
        var secondBody = _http.Requests[1].Body!;
        Assert.Contains("tool_result", secondBody);
        Assert.Contains("tu1", secondBody);
        Assert.Contains("No reminders are pending.", secondBody);
    }

    [Fact]
    public async Task AnswerPersonalQuestion_ToolFailure_IsReportedToTheModelInsteadOfThrowing()
    {
        _http.EnqueueJson(ToolUseResponse("mark_unread", new { message_id = "m1" }));
        _http.EnqueueJson(TextResponse("That didn't work, sorry."));

        var answer = await _service.AnswerPersonalQuestionAsync(
            "mark it unread", [], [], [], [], [], [],
            (_, _) => throw new InvalidOperationException("gmail exploded"));

        Assert.Equal("That didn't work, sorry.", answer);
        Assert.Contains("Error: gmail exploded", _http.Requests[1].Body!);
    }

    [Fact]
    public async Task AnswerPersonalQuestion_RunawayToolLoop_BailsOutAfterTenRounds()
    {
        _http.Route("POST /v1/messages", ToolUseResponse("list_snoozes", new { }));

        var calls = 0;
        var answer = await _service.AnswerPersonalQuestionAsync(
            "loop forever", [], [], [], [], [], [],
            (_, _) =>
            {
                calls++;
                return Task.FromResult("still nothing");
            });

        Assert.Equal("I tried to help but got stuck in a loop of actions — please check Gmail directly.", answer);
        // Room for a search -> refine -> read -> act -> answer chain: ten rounds
        Assert.Equal(10, _http.Requests.Count);
        Assert.Equal(10, calls);
    }

    [Fact]
    public async Task AnswerPersonalQuestion_BudgetCancellation_ApologizesInsteadOfThrowing()
    {
        // The wall-clock budget firing surfaces as an OperationCanceledException from
        // the HTTP layer — simulated directly, no waiting involved. The webhook then
        // relays this text to Matthew instead of dying silently.
        _http.RouteResponder("POST /v1/messages", _ => throw new TaskCanceledException("simulated budget cut-off"));

        var answer = await _service.AnswerPersonalQuestionAsync(
            "big question", [], [], [], [], [], [],
            (_, _) => Task.FromResult(""));

        Assert.Equal(
            "That's taking me longer than I can manage in one go — ask me again in a moment and I'll pick it up fresh.",
            answer);
    }

    [Fact]
    public void PersonalAnswerBudget_LeavesMarginUnderAzuresTenMinuteHardKill()
    {
        // Azure kills the invocation at 10 minutes with no catch block — the loop must
        // cut itself off early enough to still send the apology above
        Assert.True(ClaudeSummarizerService.PersonalAnswerBudget <= TimeSpan.FromMinutes(9),
            "the Q&A budget must leave at least a minute of margin before Azure's functionTimeout");
    }

    [Fact]
    public async Task AnswerPersonalQuestion_ContextRowsCarryIdsLinksAndFlags()
    {
        _http.EnqueueJson(TextResponse("ok"));

        var personalEmails = new List<ProcessedEmailEntity>
        {
            ProcessedEmail(messageId: "m77", subject: "GO bill", senderName: "GO", threadId: "t77", needsReply: true)
        };
        var actions = new List<Event>
        {
            new() { Id = "ev42", Summary = "Deadline: Pay GO invoice", Start = new EventDateTime { Date = "2026-08-25" } }
        };

        await _service.AnswerPersonalQuestionAsync("q", [], [], personalEmails, actions, [], [],
            (_, _) => Task.FromResult(""));

        var prompt = RequestUserText();
        Assert.Contains("id=m77", prompt); // Claude needs the id to drive tools
        Assert.Contains("[needs reply]", prompt);
        Assert.Contains(Alfred.Functions.Services.Gmail.GmailLinks.ForThread("t77"), prompt);
        Assert.Contains("eventId=ev42", prompt); // calendar tools take the event id
    }
}
