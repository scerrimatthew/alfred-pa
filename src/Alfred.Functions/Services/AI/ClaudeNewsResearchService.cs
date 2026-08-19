using System.Text.Json;
using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Services.AI;

public class ClaudeNewsResearchService : INewsResearchService
{
    // Server-side web search can pause its own loop (stop_reason "pause_turn"); each
    // re-send resumes it. This caps how many resumes one research run will spend.
    private const int MaxPauseTurnResumes = 5;

    private readonly ILogger<ClaudeNewsResearchService> _logger;
    private readonly AlfredOptions _options;

    public ClaudeNewsResearchService(IOptions<AlfredOptions> options, ILogger<ClaudeNewsResearchService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiNewsDigest> ResearchDailyNewsAsync(List<NewsRuleEntity> rules, List<ReportedNewsEntity> recentlyReported)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");
        var (systemPrompt, userPrompt) = BuildNewsPrompt(today, rules, recentlyReported, Math.Max(1, _options.AiNewsMaxItems));

        var messages = new List<Message> { new(RoleType.User, userPrompt) };

        var parameters = new MessageParameters
        {
            Model = "claude-opus-5",
            // Generous cap: the run carries thinking + multi-search reasoning + the digest
            MaxTokens = 16000,
            System = [new SystemMessage(systemPrompt)],
            Messages = messages,
            Tools = [ServerTools.GetWebSearchTool(maxUses: 12)]
        };

        for (var attempt = 0; attempt <= MaxPauseTurnResumes; attempt++)
        {
            var response = await client.Messages.GetClaudeMessageAsync(parameters);

            if (response.StopReason == "pause_turn")
            {
                // Server-side search loop paused mid-turn — append the partial assistant
                // turn and re-send; the server resumes where it left off. Must carry the FULL
                // content (server_tool_use / web_search_tool_result blocks included) —
                // response.Message keeps only the text blocks, which would restart the
                // research from scratch instead of resuming it
                messages.Add(new Message { Role = RoleType.Assistant, Content = response.Content });
                continue;
            }

            // With server tools the answer is the LAST text block — earlier ones are
            // search-narration ("Let me look at...") interleaved with result blocks
            var responseText = response.Content?.OfType<TextContent>().LastOrDefault()?.Text ?? "{}";

            _logger.LogInformation("AI news research complete: {Length} chars, {SearchCount} searches",
                responseText.Length, response.Usage?.ServerToolUse?.WebSearchRequests ?? 0);

            return ParseNewsResponse(responseText);
        }

        _logger.LogWarning("AI news research still paused after {Max} resumes — giving up for today", MaxPauseTurnResumes);
        return new AiNewsDigest();
    }

    // Test seam: when set, replaces the live client (tests back it with a fake
    // HttpClient). Never set in production.
    internal Func<AnthropicClient>? ClientFactory { get; set; }

    private AnthropicClient CreateClient()
    {
        if (ClientFactory is not null) return ClientFactory();

        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
            ?? throw new InvalidOperationException("Anthropic API key not configured");

        return new AnthropicClient(apiKey);
    }

    internal static (string System, string User) BuildNewsPrompt(
        string today,
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported,
        int maxItems)
    {
        var feedbackSection = rules.Count > 0
            ? string.Join("\n", rules.OrderBy(r => r.CreatedAt).Select(r =>
                $"- [{r.RowKey}] {r.Instruction} (added {r.CreatedAt:d MMM yyyy})"))
            : "None yet.";

        var coveredSection = recentlyReported.Count > 0
            ? string.Join("\n", recentlyReported.OrderByDescending(r => r.ReportedAt).Select(r =>
                $"- [{r.ReportedAt:d MMM}] {r.Headline} ({r.Url})"))
            : "Nothing reported yet.";

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant. One of your jobs is a short evening
            AI-news briefing. Matthew co-owns Cleverbit Software, and the briefing exists to
            serve the company's strategic bet, described in the WATCHLIST below. You research
            today's AI news with web search and report only what genuinely matters.

            Today is {today}.

            ═══ WATCHLIST (the standing brief — what is relevant and what is noise) ═══

            {AiNewsBriefing.Watchlist}

            ═══ MATTHEW'S STANDING FEEDBACK (overrides the watchlist where they conflict) ═══

            {feedbackSection}

            How to research:
            - Run several targeted web searches across the watchlist categories. Focus on the
              last 24-48 hours; a slightly older story is only worth including if it is
              significant and clearly not yet covered.
            - Prefer primary sources (the study, the announcement, the ruling) over aggregator
              rewrites; link the best single source per story.
            - Verify a story is real before reporting it — if only one low-quality source
              carries a dramatic claim, either skip it or say it is unconfirmed.
            - The ALREADY COVERED list shows what Matthew has been told before. Skip those
              stories unless the implication has genuinely changed (new ruling, reversal,
              major follow-up) — and then frame the item as an update.

            Respond with valid JSON only, no markdown code fences, with exactly these fields:

            1. "items": array of at most {maxItems} stories, ranked most relevant first —
               relevance to the watchlist, not completeness. An empty array is the correct
               answer on a quiet day; never pad with borderline noise. Each item:
               - "headline": short headline in your own words
               - "url": link to the best source
               - "category": one of "thesis-evidence", "competitor", "anthropic",
                 "regulatory", "buyer-pain", "tl-material"
               - "summary": one sentence on what happened
               - "whyItMatters": one sentence tying it to the specific strand of the vision

            2. "telegramMessage": the briefing as Matthew reads it, or null when items is
               empty. Write it the way a sharp human PA would text an evening round-up:
               - Open with a one-line greeting starting with the 🗞 emoji and a space.
               - Then one short block per item: the headline as a link —
                 <a href="url">headline</a> — followed by the summary and the why-it-matters
                 in plain conversational prose. Bold only the facts that matter
                 (<b>names, numbers, deadlines</b>).
               - Flag disconfirming evidence on the core thesis explicitly and first — never
                 bury it ("⚠️ heads up — this one cuts against the thesis...").
               - Tag thought-leadership material in passing ("could be TL material for Eman
                 or Simon").
               - If a story demands action or a decision, say so and name whose court it
                 lands in.
               - Keep it glanceable — a few lines per item, no headers, no separator lines.
               - Only use <b> and <a href=""> tags. Outside those tags, headlines and prose
                 pulled from the web must not contain raw <, > or & characters — rephrase or
                 escape them (&lt; &gt; &amp;) or the message will fail to send.
            """;

        var userPrompt = $"""
            ## ALREADY COVERED (do not repeat unless the implication changed)
            {coveredSection}

            Research today's AI news and respond with the JSON described in your instructions.
            """;

        return (systemPrompt, userPrompt);
    }

    internal static AiNewsDigest ParseNewsResponse(string json)
    {
        json = json.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0)
                json = json[(firstNewline + 1)..];
            if (json.EndsWith("```"))
                json = json[..^3];
            json = json.Trim();
        }

        // Let a malformed response throw — the digest function reports the failure to
        // Matthew instead of silently skipping the day
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var digest = new AiNewsDigest
        {
            TelegramMessage = root.TryGetProperty("telegramMessage", out var tmProp) && tmProp.ValueKind == JsonValueKind.String
                ? tmProp.GetString()
                : null
        };

        if (root.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                var headline = item.TryGetProperty("headline", out var hProp) ? hProp.GetString() : null;
                var url = item.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(headline) || string.IsNullOrWhiteSpace(url))
                    continue;

                digest.Items.Add(new AiNewsItem
                {
                    Headline = headline,
                    Url = url,
                    Category = item.TryGetProperty("category", out var cProp) && cProp.ValueKind == JsonValueKind.String
                        ? cProp.GetString()
                        : null
                });
            }
        }

        return digest;
    }
}
