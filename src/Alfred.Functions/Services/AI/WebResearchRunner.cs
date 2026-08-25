using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;

namespace Alfred.Functions.Services.AI;

// Shared plumbing for the server-side-web-search research runs (the AI-news briefings and
// the weekly ETF report): one long-timeout HttpClient, one wall-clock budget, and the
// pause_turn resume loop. Each service keeps its own client factory / test seam.
internal static class WebResearchRunner
{
    // Server-side web search can pause its own loop (stop_reason "pause_turn"); each
    // re-send resumes it. This caps how many resumes one research run will spend.
    private const int MaxPauseTurnResumes = 5;

    // Wall-clock budget for a whole research run. Azure kills the invocation hard at the
    // 10-minute functionTimeout — past that no catch block runs and the caller can't even
    // apologize — so the run must cut itself off with margin to parse, send, and clean up.
    internal static readonly TimeSpan Budget = TimeSpan.FromMinutes(7.5);

    // The SDK's default HttpClient times out at 100 seconds — an opus + multi-search
    // request routinely runs longer. One shared client (the services are singletons and
    // the AnthropicClient is never disposed) with a ceiling above the research budget,
    // so the per-run cancellation token is what actually governs.
    internal static readonly HttpClient LongRunHttpClient = new() { Timeout = TimeSpan.FromMinutes(9) };

    // Runs one web-search research conversation to completion, resuming pause_turn stops.
    // Returns the final text answer, or null when the run never completed (budget spent
    // or resume cap hit) — callers report that as an incomplete run, not an empty result.
    internal static async Task<string?> RunAsync(
        AnthropicClient client,
        string systemPrompt,
        string userPrompt,
        int maxSearches,
        string runLabel,
        ILogger logger)
    {
        var messages = new List<Message> { new(RoleType.User, userPrompt) };

        var parameters = new MessageParameters
        {
            Model = "claude-opus-5",
            // Generous cap: the run carries thinking + multi-search reasoning + the write-up
            MaxTokens = 16000,
            System = [new SystemMessage(systemPrompt)],
            Messages = messages,
            Tools = [ServerTools.GetWebSearchTool(maxUses: maxSearches)]
        };

        using var budget = new CancellationTokenSource(Budget);
        try
        {
            for (var attempt = 0; attempt <= MaxPauseTurnResumes; attempt++)
            {
                var response = await client.Messages.GetClaudeMessageAsync(parameters, budget.Token);

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

                logger.LogInformation("Web research ({Run}) complete: {Length} chars, {SearchCount} searches",
                    runLabel, responseText.Length, response.Usage?.ServerToolUse?.WebSearchRequests ?? 0);

                return responseText;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Web research ({Run}) exceeded its {Budget}-minute budget — cut off",
                runLabel, Budget.TotalMinutes);
            return null;
        }

        logger.LogWarning("Web research ({Run}) still paused after {Max} resumes — giving up", runLabel, MaxPauseTurnResumes);
        return null;
    }

    // Strips a ```json fence if the model wrapped its JSON in one
    internal static string StripCodeFence(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```"))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline > 0)
            text = text[(firstNewline + 1)..];
        if (text.EndsWith("```"))
            text = text[..^3];
        return text.Trim();
    }
}
