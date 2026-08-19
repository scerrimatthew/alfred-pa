using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Pins the parsing of Claude's JSON replies — the safety net between a flaky LLM
// response and a silently dropped invoice.
public class ClaudeSummarizerParsingTests
{
    private static EmailDigest ParseDigest(string json) =>
        ClaudeSummarizerService.ParseDigestResponse(json);

    private static PersonalEmailTriage ParseTriage(string json, SchoolEmail? email = null) =>
        ClaudeSummarizerService.ParseTriageResponse(json, email ?? Email());

    // ---- School email digests ----

    [Fact]
    public void Digest_HappyPath_ParsesAllFields()
    {
        var digest = ParseDigest("""
            {
                "telegramMessage": "📩 <b>SPORTS DAY</b>",
                "calendarEvents": [
                    {
                        "title": "Outing: Zoo Year 1",
                        "description": "Bring a hat",
                        "date": "2026-09-10",
                        "startTime": "09:30",
                        "endTime": "12:00",
                        "action": "create"
                    }
                ],
                "homework": "Read pages 1-3 by Friday",
                "requiresImmediateAlert": true,
                "category": "outing"
            }
            """);

        Assert.Equal("📩 <b>SPORTS DAY</b>", digest.TelegramMessage);
        Assert.Equal("Read pages 1-3 by Friday", digest.Homework);
        Assert.True(digest.RequiresImmediateAlert);
        Assert.Equal("outing", digest.Category);

        var ev = Assert.Single(digest.CalendarEvents);
        Assert.Equal("Outing: Zoo Year 1", ev.Title);
        Assert.Equal("Bring a hat", ev.Description);
        Assert.Equal(new DateTime(2026, 9, 10), ev.Date);
        Assert.Equal(new TimeSpan(9, 30, 0), ev.StartTime);
        Assert.Equal(new TimeSpan(12, 0, 0), ev.EndTime);
        Assert.Equal(CalendarEventAction.Create, ev.Action);
        Assert.False(ev.IsAllDay);
    }

    [Fact]
    public void Digest_MissingOptionalFields_UsesSafeDefaults()
    {
        var digest = ParseDigest("""{"telegramMessage": "hello"}""");

        Assert.Equal("hello", digest.TelegramMessage);
        Assert.Null(digest.Homework);
        Assert.False(digest.RequiresImmediateAlert);
        Assert.Equal("other", digest.Category);
        Assert.Empty(digest.CalendarEvents);
    }

    [Fact]
    public void Digest_NullHomeworkAndNullTimes_MapToNulls()
    {
        var digest = ParseDigest("""
            {
                "telegramMessage": "m",
                "homework": null,
                "calendarEvents": [
                    {"title": "Field Day", "description": "", "date": "2026-06-01", "startTime": null, "endTime": null, "action": "create"}
                ]
            }
            """);

        Assert.Null(digest.Homework);
        var ev = Assert.Single(digest.CalendarEvents);
        Assert.Null(ev.StartTime);
        Assert.True(ev.IsAllDay);
    }

    [Theory]
    [InlineData("UPDATE", CalendarEventAction.Update)]
    [InlineData("delete", CalendarEventAction.Delete)]
    [InlineData("not-a-real-action", CalendarEventAction.Create)] // unknown falls back to create
    public void Digest_EventAction_IsParsedCaseInsensitivelyWithCreateFallback(string action, CalendarEventAction expected)
    {
        var digest = ParseDigest($$"""
            {
                "telegramMessage": "m",
                "calendarEvents": [
                    {"title": "T", "description": "", "date": "2026-06-01", "action": "{{action}}"}
                ]
            }
            """);

        Assert.Equal(expected, Assert.Single(digest.CalendarEvents).Action);
    }

    [Fact]
    public void Digest_MarkdownCodeFences_AreStripped()
    {
        var digest = ParseDigest("```json\n{\"telegramMessage\": \"fenced\"}\n```");

        Assert.Equal("fenced", digest.TelegramMessage);
    }

    [Fact]
    public void Digest_TruncatedJson_RecoversTheTelegramMessage()
    {
        // Simulates a max-tokens truncation right after the message value
        var digest = ParseDigest("""
            {"telegramMessage": "📩 <b>IMPORTANT</b>\nLine two",
              "calendarEvents": [{"title": "half an eve
            """);

        Assert.Equal("📩 <b>IMPORTANT</b>\nLine two", digest.TelegramMessage);
        Assert.Empty(digest.CalendarEvents);
    }

    [Fact]
    public void Digest_CompletelyUnusableResponse_FallsBackToACheckGmailMessage()
    {
        var digest = ParseDigest("Sorry, I can't help with that.");

        Assert.Contains("could not parse", digest.TelegramMessage);
        Assert.Empty(digest.CalendarEvents);
    }

    // ---- Personal triage ----

    [Fact]
    public void Triage_HappyPath_ParsesAllFields()
    {
        var triage = ParseTriage("""
            {
                "suppressed": false,
                "matchedRule": null,
                "requiresAttention": true,
                "matchedAttentionRule": null,
                "category": "invoice",
                "summary": "GO bill for August, €45.20 due 25 Aug.",
                "telegramMessage": "Your GO bill is in — <b>€45.20</b>.",
                "calendarEvents": [
                    {"title": "Deadline: Pay GO invoice", "description": "€45.20", "date": "2026-08-25", "startTime": null, "endTime": null, "action": "create"}
                ],
                "fraudWarning": null,
                "needsReply": false
            }
            """);

        Assert.True(triage.RequiresAttention);
        Assert.False(triage.Suppressed);
        Assert.Equal("invoice", triage.Category);
        Assert.Equal("GO bill for August, €45.20 due 25 Aug.", triage.Summary);
        Assert.Equal("Your GO bill is in — <b>€45.20</b>.", triage.TelegramMessage);
        Assert.Null(triage.FraudWarning);
        Assert.False(triage.NeedsReply);
        var ev = Assert.Single(triage.CalendarEvents);
        Assert.Equal(new DateTime(2026, 8, 25), ev.Date);
    }

    [Fact]
    public void Triage_SuppressedByRule_CarriesTheRuleId()
    {
        var triage = ParseTriage("""
            {"suppressed": true, "matchedRule": "r1", "requiresAttention": false, "category": "notification", "summary": "s"}
            """);

        Assert.True(triage.Suppressed);
        Assert.Equal("r1", triage.MatchedRule);
        Assert.False(triage.RequiresAttention);
    }

    [Fact]
    public void Triage_AttentionRule_ForcesNotificationAndBeatsSuppression()
    {
        var triage = ParseTriage("""
            {"suppressed": true, "matchedRule": "r1", "requiresAttention": false, "matchedAttentionRule": "a1", "category": "financial", "summary": "s"}
            """);

        Assert.True(triage.RequiresAttention, "attention rule must force attention");
        Assert.False(triage.Suppressed, "attention rule must beat suppression");
        Assert.Equal("a1", triage.MatchedAttentionRule);
    }

    [Fact]
    public void Triage_FraudWarning_ForcesNotificationAndBeatsSuppression()
    {
        var triage = ParseTriage("""
            {"suppressed": true, "requiresAttention": false, "category": "payment-request", "summary": "s",
             "fraudWarning": "Claims to be BOV but was sent from bov-alerts.net."}
            """);

        Assert.True(triage.RequiresAttention, "a fraud warning must always notify");
        Assert.False(triage.Suppressed, "a fraud warning must override suppression");
        Assert.Equal("Claims to be BOV but was sent from bov-alerts.net.", triage.FraudWarning);
    }

    [Fact]
    public void Triage_NeedsReply_IsParsed()
    {
        var triage = ParseTriage("""
            {"requiresAttention": true, "category": "personal-reply", "summary": "s", "needsReply": true}
            """);

        Assert.True(triage.NeedsReply);
    }

    [Fact]
    public void Triage_MissingFields_FallBackToSubjectAndOther()
    {
        var email = Email(subject: "The subject line");
        var triage = ParseTriage("{}", email);

        Assert.False(triage.RequiresAttention);
        Assert.Equal("other", triage.Category);
        Assert.Equal("The subject line", triage.Summary);
        Assert.Equal("", triage.TelegramMessage);
    }

    [Fact]
    public void Triage_UnparseableResponse_FailsOpenToANotification()
    {
        var email = Email(subject: "Suspicious invoice", senderName: "ACME");
        var triage = ParseTriage("not json", email);

        // Fail open: better a redundant notification than a silently dropped invoice
        Assert.True(triage.RequiresAttention);
        Assert.Equal("Suspicious invoice", triage.Summary);
        Assert.Contains("could not summarize", triage.TelegramMessage);
        Assert.Contains("ACME", triage.TelegramMessage);
        Assert.False(triage.Suppressed);
    }

    [Fact]
    public void Triage_CodeFences_AreStripped()
    {
        var triage = ParseTriage("```json\n{\"requiresAttention\": true, \"summary\": \"s\"}\n```");

        Assert.True(triage.RequiresAttention);
        Assert.Equal("s", triage.Summary);
    }

    // ---- Newsletter-mined news leads ----

    [Fact]
    public void Triage_NewsLeads_AreParsedWithUrlAndNote()
    {
        var triage = ParseTriage("""
            {"requiresAttention": false, "category": "notification", "summary": "s",
             "newsLeads": [
                {"headline": "DORA 2026 lands", "url": "https://dora.dev/2026", "note": "Review times doubled"},
                {"headline": "Bare lead"}
             ]}
            """);

        Assert.Equal(2, triage.NewsLeads.Count);
        Assert.Equal("DORA 2026 lands", triage.NewsLeads[0].Headline);
        Assert.Equal("https://dora.dev/2026", triage.NewsLeads[0].Url);
        Assert.Equal("Review times doubled", triage.NewsLeads[0].Note);
        Assert.Equal("Bare lead", triage.NewsLeads[1].Headline);
        Assert.Null(triage.NewsLeads[1].Url);
        Assert.Null(triage.NewsLeads[1].Note);
    }

    [Fact]
    public void Triage_NewsLeadsWithoutAHeadline_AreSkipped()
    {
        var triage = ParseTriage("""
            {"summary": "s", "newsLeads": [
                {"url": "https://no-headline.example"},
                {"headline": "   ", "url": "https://blank.example"},
                {"headline": null},
                {"headline": "Keeper"}
            ]}
            """);

        Assert.Equal("Keeper", Assert.Single(triage.NewsLeads).Headline);
    }

    [Theory]
    [InlineData("""{"summary": "s"}""")]                       // field missing entirely
    [InlineData("""{"summary": "s", "newsLeads": null}""")]    // null
    [InlineData("""{"summary": "s", "newsLeads": "none"}""")]  // not an array
    public void Triage_MissingOrMalformedNewsLeads_MeanNoLeads(string json)
    {
        Assert.Empty(ParseTriage(json).NewsLeads);
    }

    [Fact]
    public void Triage_NullLeadUrlAndNote_StayNull()
    {
        var triage = ParseTriage("""
            {"summary": "s", "newsLeads": [{"headline": "H", "url": null, "note": null}]}
            """);

        var lead = Assert.Single(triage.NewsLeads);
        Assert.Null(lead.Url);
        Assert.Null(lead.Note);
    }

    // ---- Conversation history block ----

    [Fact]
    public void ConversationSection_Empty_ProducesNothing()
    {
        var section = ClaudeSummarizerService.FormatConversationSection(new List<ChatTurnEntity>());

        Assert.Equal(string.Empty, section);
    }

    [Fact]
    public void ConversationSection_RendersMaltaTimestampsAndPriorityCaveat()
    {
        var turns = new List<ChatTurnEntity>
        {
            new()
            {
                Question = "what's due?",
                Answer = "The GO bill.",
                // 10:00 UTC on a July day = 12:00 Malta (CEST)
                AskedAt = new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero)
            }
        };

        var section = ClaudeSummarizerService.FormatConversationSection(turns);

        Assert.Contains("RECENT CONVERSATION", section);
        Assert.Contains("12:00] Q: what's due?", section);
        Assert.Contains("12:00] A: The GO bill.", section);
        Assert.Contains("takes priority", section);
    }
}
