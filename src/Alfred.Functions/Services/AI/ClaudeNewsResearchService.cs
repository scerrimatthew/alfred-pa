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
    // Wall-clock budget for a whole research run, and the pause_turn resume loop itself,
    // are shared with the weekly ETF report — see WebResearchRunner
    internal static readonly TimeSpan ResearchBudget = WebResearchRunner.Budget;

    private readonly ILogger<ClaudeNewsResearchService> _logger;
    private readonly AlfredOptions _options;

    public ClaudeNewsResearchService(IOptions<AlfredOptions> options, ILogger<ClaudeNewsResearchService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiNewsDigest> ResearchDailyNewsAsync(
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported,
        List<NewsCandidateEntity> newsletterCandidates,
        string? topic = null)
    {
        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");
        var (systemPrompt, userPrompt) = BuildNewsPrompt(
            today, rules, recentlyReported, newsletterCandidates, Math.Max(1, _options.AiNewsMaxItems), topic);

        // Opus for the evening digest: ranking a day of news against the watchlist is
        // the judgment call the whole feature exists for
        var responseText = await RunResearchAsync("claude-opus-5", systemPrompt, userPrompt, maxSearches: 12, "daily digest");
        return responseText is null ? new AiNewsDigest { Incomplete = true } : ParseNewsResponse(responseText);
    }

    public async Task<AiNewsDigest> CheckUrgentNewsAsync(
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported)
    {
        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");
        var (systemPrompt, userPrompt) = BuildFlashPrompt(today, rules, recentlyReported);

        // Sonnet for the flash check: it runs every day and almost always concludes
        // "nothing urgent" — a miss just waits for the evening Opus digest anyway
        var responseText = await RunResearchAsync("claude-sonnet-5", systemPrompt, userPrompt, maxSearches: 6, "midday flash check");
        return responseText is null ? new AiNewsDigest { Incomplete = true } : ParseNewsResponse(responseText);
    }

    public async Task<string?> BuildWeeklySynthesisAsync(
        List<ReportedNewsEntity> weekItems,
        List<NewsRuleEntity> rules)
    {
        var client = CreateClient();

        var today = DateTime.Now.ToString("dddd, d MMMM yyyy");
        var (systemPrompt, userPrompt) = BuildWeeklyPrompt(today, weekItems, rules);

        var parameters = new MessageParameters
        {
            Model = "claude-opus-5",
            MaxTokens = 4096,
            System = [new SystemMessage(systemPrompt)],
            Messages = [new Message(RoleType.User, userPrompt)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);
        var responseText = response.Content?.OfType<TextContent>().LastOrDefault()?.Text;

        _logger.LogInformation("Weekly AI-news synthesis generated: {Length} chars", responseText?.Length ?? 0);
        return string.IsNullOrWhiteSpace(responseText) ? null : responseText.Trim();
    }

    private Task<string?> RunResearchAsync(string model, string systemPrompt, string userPrompt, int maxSearches, string runLabel) =>
        WebResearchRunner.RunAsync(CreateClient(), model, systemPrompt, userPrompt, maxSearches, runLabel, _logger);

    // Test seam: when set, replaces the live client (tests back it with a fake
    // HttpClient). Never set in production.
    internal Func<AnthropicClient>? ClientFactory { get; set; }

    private AnthropicClient CreateClient()
    {
        if (ClientFactory is not null) return ClientFactory();

        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
            ?? throw new InvalidOperationException("Anthropic API key not configured");

        return new AnthropicClient(apiKey, WebResearchRunner.LongRunHttpClient);
    }

    internal static string FormatFeedbackSection(List<NewsRuleEntity> rules) =>
        rules.Count > 0
            ? string.Join("\n", rules.OrderBy(r => r.CreatedAt).Select(r =>
                $"- [{r.RowKey}] {r.Instruction} (added {r.CreatedAt:d MMM yyyy})"))
            : "None yet.";

    internal static string FormatCoveredSection(List<ReportedNewsEntity> recentlyReported) =>
        recentlyReported.Count > 0
            ? string.Join("\n", recentlyReported.OrderByDescending(r => r.ReportedAt).Select(r =>
                $"- [{r.ReportedAt:d MMM}] {r.Headline} ({r.Url})"))
            : "Nothing reported yet.";

    // The JSON contract shared by the daily digest and the flash check
    private const string ItemsJsonSpec = """
           - "headline": short headline in your own words
           - "url": link to the best source
           - "category": one of "thesis-evidence", "competitor", "anthropic",
             "regulatory", "buyer-pain", "tl-material"
           - "summary": one sentence on what happened
           - "whyItMatters": one sentence tying it to the specific strand of the vision
        """;

    internal static (string System, string User) BuildNewsPrompt(
        string today,
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported,
        List<NewsCandidateEntity> newsletterCandidates,
        int maxItems,
        string? topic = null)
    {
        var feedbackSection = FormatFeedbackSection(rules);
        var coveredSection = FormatCoveredSection(recentlyReported);

        var candidatesSection = newsletterCandidates.Count > 0
            ? "\n\n## CANDIDATE STORIES (mined from AI newsletters in Matthew's inbox — leads only, "
              + "not verified; check any that look watchlist-relevant and include the ones that clear "
              + "the bar, linking the primary source rather than the newsletter)\n"
              + string.Join("\n", newsletterCandidates.OrderByDescending(c => c.SeenAt).Select(c =>
              {
                  var url = !string.IsNullOrWhiteSpace(c.Url) ? $" ({c.Url})" : "";
                  var note = !string.IsNullOrWhiteSpace(c.Note) ? $" — {c.Note}" : "";
                  return $"- [{c.Source}] {c.Headline}{url}{note}";
              }))
            : "";

        var topicSection = !string.IsNullOrWhiteSpace(topic)
            ? $"""


              ═══ TARGETED SWEEP ═══

              Matthew asked for an on-demand sweep on: "{topic}".
              Focus every search on that topic. The watchlist still frames why a story
              matters, but for this run topic relevance beats watchlist breadth, and the
              time window may extend beyond 72 hours when the topic needs the context.
              """
            : "";

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant. One of your jobs is a short evening
            AI-news briefing. Matthew co-owns Cleverbit Software, and the briefing exists to
            serve the company's strategic bet, described in the WATCHLIST below. You research
            today's AI news with web search and report only what genuinely matters.

            Today is {today}.

            ═══ WATCHLIST (the standing brief — what is relevant and what is noise) ═══

            {AiNewsBriefing.Watchlist}

            ═══ MATTHEW'S STANDING FEEDBACK (overrides the watchlist where they conflict) ═══

            {feedbackSection}{topicSection}

            How to research:
            - Run several targeted web searches across the watchlist categories. Focus on the
              last 24-72 hours — this briefing runs every other day, so that fully covers the
              gap since the last one; a slightly older story is only worth including if it is
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
            {ItemsJsonSpec}

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
            {coveredSection}{candidatesSection}

            Research today's AI news and respond with the JSON described in your instructions.
            """;

        return (systemPrompt, userPrompt);
    }

    internal static (string System, string User) BuildFlashPrompt(
        string today,
        List<NewsRuleEntity> rules,
        List<ReportedNewsEntity> recentlyReported)
    {
        var feedbackSection = FormatFeedbackSection(rules);
        var coveredSection = FormatCoveredSection(recentlyReported);

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant. This is NOT the evening news digest —
            it is a quick midday check for stories so urgent they should not wait for tonight's
            briefing. Matthew co-owns Cleverbit Software; the WATCHLIST below is the standing
            brief. Almost every run of this check should find nothing.

            Today is {today}.

            ═══ WATCHLIST (context for judging urgency) ═══

            {AiNewsBriefing.Watchlist}

            ═══ MATTHEW'S STANDING FEEDBACK (overrides the watchlist where they conflict) ═══

            {feedbackSection}

            A story clears THIS check only if it broke in roughly the last 24 hours AND is one of:
            - A competitor launching directly into the A-SDLC niche — a named firm announcing a
              productised AI-era software-delivery method, AI code-governance service, or
              agentic-SDLC offering.
            - An Anthropic partner-program change WITH a deadline or a closing window (application
              dates, certification requirements taking effect, a tier opening or closing).
            - Thesis-level disconfirming evidence — a credible study or dataset showing agent-speed
              generation WITHOUT the governance cost the thesis predicts.
            - Regulation with a compliance clock — a rule, ruling, or enforcement action that
              starts a concrete countdown relevant to the beachhead or Inscope.

            Everything else — including stories that would comfortably make the evening digest —
            waits for tonight. When in doubt, it waits. Run at most a few targeted searches; this
            is a spot check, not a sweep.

            Respond with valid JSON only, no markdown code fences, with exactly these fields:

            1. "items": array of at most 3 stories meeting the bar above — an empty array is the
               expected answer on a normal day. Each item:
            {ItemsJsonSpec}

            2. "telegramMessage": the alert as Matthew reads it, or null when items is empty:
               - Open with "🚨 " and one line saying why this couldn't wait for the evening.
               - Then one short block per item: <a href="url">headline</a>, what happened, why it
                 matters, and — since these are flag-level — what decision or action it touches
                 and whose court it lands in.
               - Bold the facts that matter (<b>names, dates, deadlines</b>).
               - Only use <b> and <a href=""> tags. Outside those tags, no raw <, > or &
                 characters — rephrase or escape them (&lt; &gt; &amp;).
            """;

        var userPrompt = $"""
            ## ALREADY COVERED (do not re-flag these)
            {coveredSection}

            Run the midday urgency check and respond with the JSON described in your instructions.
            """;

        return (systemPrompt, userPrompt);
    }

    internal static (string System, string User) BuildWeeklyPrompt(
        string today,
        List<ReportedNewsEntity> weekItems,
        List<NewsRuleEntity> rules)
    {
        var feedbackSection = FormatFeedbackSection(rules);

        var itemsSection = string.Join("\n", weekItems.OrderBy(r => r.ReportedAt).Select(r =>
        {
            var summary = !string.IsNullOrWhiteSpace(r.Summary) ? $": {r.Summary}" : "";
            var why = !string.IsNullOrWhiteSpace(r.WhyItMatters) ? $" | why it mattered: {r.WhyItMatters}" : "";
            return $"- [{r.ReportedAt:ddd d MMM}] [{r.Category ?? "uncategorized"}] {r.Headline} ({r.Url}){summary}{why}";
        }));

        var systemPrompt = $"""
            You are Alfred, Matthew's personal assistant. Every Friday you close the week with a
            short "the week in AI vs the thesis" synthesis for Matthew, who co-owns Cleverbit
            Software. The WATCHLIST below is the standing brief; the stories listed by the user
            are everything you reported to him this week. Your job now is NOT to re-summarize
            them — he has read them — but to connect the dots.

            Today is {today}.

            ═══ WATCHLIST (the standing brief) ═══

            {AiNewsBriefing.Watchlist}

            ═══ MATTHEW'S STANDING FEEDBACK ═══

            {feedbackSection}

            Write the weekly synthesis:
            - Open with one line starting "🗞 " framing the week in a phrase.
            - Then walk the vision strands that actually moved this week (thesis evidence,
              competitors, Anthropic, regulation, buyer pain) — for each, what the week's items
              add up to and what it means for the bet. Skip strands with nothing to say.
            - Call the balance on the core thesis honestly: did this week strengthen it, dent
              it, or leave it untouched? Disconfirming signals come first, never buried.
            - Close with at most two things worth raising in the next EOS/leadership discussion,
              if the week produced any — otherwise say the week demands nothing.
            - Reference stories inline as <a href="url">short name</a> where useful; no need to
              link everything.
            - Glanceable prose, no headers or separator lines; a light week deserves a short
              synthesis. Bold sparingly (<b>the conclusions</b>, not the plumbing).
            - Only use <b> and <a href=""> tags. Outside those tags, no raw <, > or & characters
              — rephrase or escape them (&lt; &gt; &amp;).

            Respond with the formatted HTML message only — no JSON, no code fences.
            """;

        var userPrompt = $"""
            ## STORIES REPORTED THIS WEEK
            {itemsSection}

            Write this week's synthesis.
            """;

        return (systemPrompt, userPrompt);
    }

    internal static AiNewsDigest ParseNewsResponse(string json)
    {
        json = WebResearchRunner.StripCodeFence(json);

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
                        : null,
                    Summary = item.TryGetProperty("summary", out var sProp) && sProp.ValueKind == JsonValueKind.String
                        ? sProp.GetString()
                        : null,
                    WhyItMatters = item.TryGetProperty("whyItMatters", out var wProp) && wProp.ValueKind == JsonValueKind.String
                        ? wProp.GetString()
                        : null
                });
            }
        }

        return digest;
    }
}
