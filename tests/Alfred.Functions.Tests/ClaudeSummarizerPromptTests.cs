using Alfred.Functions.Models;
using Alfred.Functions.Services.AI;
using Xunit;
using static Alfred.Functions.Tests.Support.TestData;

namespace Alfred.Functions.Tests;

// Pins the load-bearing parts of the prompts: which context sections appear, the
// body-size cap, and the date the model is told to resolve relative words against.
public class ClaudeSummarizerPromptTests
{
    private static string BuildTriagePrompt(
        SchoolEmail email,
        List<SuppressionRuleEntity>? suppressionRules = null,
        List<AttentionRuleEntity>? attentionRules = null,
        List<UserFactEntity>? userFacts = null,
        List<ProcessedEmailEntity>? threadContext = null)
    {
        return ClaudeSummarizerService.BuildTriagePrompt(
            email, "Wednesday, 19 August 2026", "",
            suppressionRules ?? [], attentionRules ?? [], userFacts ?? [], threadContext ?? []);
    }

    [Fact]
    public void TriagePrompt_CarriesEmailDetailsAndSendDate()
    {
        var email = Email(
            subject: "August bill",
            senderName: "GO",
            senderEmail: "billing@go.com.mt",
            body: "Please pay €45.20",
            receivedDate: new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero));

        var prompt = BuildTriagePrompt(email);

        Assert.Contains("Email Subject: August bill", prompt);
        Assert.Contains("From: GO <billing@go.com.mt>", prompt);
        Assert.Contains("Please pay €45.20", prompt);
        // Relative dates must resolve against the send date, stated as an ISO date
        Assert.Contains("2026-08-17", prompt);
        // No rules, facts, or thread context configured — those sections must be absent
        Assert.DoesNotContain("SUPPRESSION RULES", prompt);
        Assert.DoesNotContain("ATTENTION RULES", prompt);
        Assert.DoesNotContain("THINGS MATTHEW HAS TOLD ALFRED", prompt);
        Assert.DoesNotContain("THREAD CONTEXT", prompt);
    }

    [Fact]
    public void TriagePrompt_HugeBody_IsCappedAt8000Chars()
    {
        var email = Email(body: new string('x', 9000));

        var prompt = BuildTriagePrompt(email);

        Assert.Contains(new string('x', 8000) + "\n[... truncated]", prompt);
        Assert.DoesNotContain(new string('x', 8001), prompt);
    }

    [Fact]
    public void TriagePrompt_SuppressionRules_AreListedWithIdsAndExamples()
    {
        var rules = new List<SuppressionRuleEntity>
        {
            new() { RowKey = "r1", Pattern = "Monthly Bolt reports", ExampleSender = "reports@bolt.eu", ExampleSubject = "July report" }
        };

        var prompt = BuildTriagePrompt(Email(), suppressionRules: rules);

        Assert.Contains("SUPPRESSION RULES", prompt);
        Assert.Contains("[r1] Monthly Bolt reports", prompt);
        Assert.Contains("reports@bolt.eu", prompt);
        Assert.Contains("July report", prompt);
    }

    [Fact]
    public void TriagePrompt_AttentionRules_AreListedAndDeclaredToBeatSuppression()
    {
        var rules = new List<AttentionRuleEntity>
        {
            new() { RowKey = "a1", Pattern = "Anything from HSBC" }
        };

        var prompt = BuildTriagePrompt(Email(), attentionRules: rules);

        Assert.Contains("ATTENTION RULES", prompt);
        Assert.Contains("[a1] Anything from HSBC", prompt);
        Assert.Contains("Attention rules WIN over suppression rules", prompt);
    }

    [Fact]
    public void TriagePrompt_UserFacts_AreListedWithTheFileQuietlyGuidance()
    {
        var facts = new List<UserFactEntity>
        {
            new() { RowKey = "f1", Fact = "Matthew's apartment at Hillcrest is A5 in Block A." },
            new() { RowKey = "f2", Fact = "There is no Netflix subscription anymore." }
        };

        var prompt = BuildTriagePrompt(Email(), userFacts: facts);

        Assert.Contains("THINGS MATTHEW HAS TOLD ALFRED", prompt);
        Assert.Contains("- Matthew's apartment at Hillcrest is A5 in Block A.", prompt);
        Assert.Contains("- There is no Netflix subscription anymore.", prompt);
        // The contract: a fact showing the email doesn't apply means a quiet filing,
        // and facts are never stretched beyond what they say
        Assert.Contains("file it quietly", prompt);
        Assert.Contains("Never stretch a fact beyond what it says.", prompt);
    }

    [Fact]
    public void TriagePrompt_FactsComeBeforeTheRuleSections()
    {
        var facts = new List<UserFactEntity> { new() { RowKey = "f1", Fact = "A fact." } };
        var rules = new List<SuppressionRuleEntity> { new() { RowKey = "r1", Pattern = "Bolt reports" } };

        var prompt = BuildTriagePrompt(Email(), suppressionRules: rules, userFacts: facts);

        Assert.True(
            prompt.IndexOf("THINGS MATTHEW HAS TOLD ALFRED", StringComparison.Ordinal)
                < prompt.IndexOf("SUPPRESSION RULES", StringComparison.Ordinal),
            "facts must be presented before the rule sections they inform");
    }

    [Fact]
    public void TriagePrompt_ThreadContext_MarksTheEmailAsAFollowUp()
    {
        var threadContext = new List<ProcessedEmailEntity>
        {
            ProcessedEmail(subject: "Quote request", senderName: "Antonio", summary: "Asked for a quote.")
        };

        var prompt = BuildTriagePrompt(Email(), threadContext: threadContext);

        Assert.Contains("THREAD CONTEXT", prompt);
        Assert.Contains("Antonio — Quote request: Asked for a quote.", prompt);
        Assert.Contains("FOLLOW-UP", prompt);
    }

    [Fact]
    public void TriagePrompt_AsksForNewsLeadsFromAiNewsletters_WithoutAffectingAttention()
    {
        var prompt = BuildTriagePrompt(Email());

        Assert.Contains("newsLeads", prompt);
        // The contract: only genuine stories, and a lead-rich newsletter still files quietly
        Assert.Contains("skip the newsletter's own promotions", prompt);
        Assert.Contains("never affects requiresAttention", prompt);
    }

    [Fact]
    public void SummarizePrompt_CarriesEmailDocumentAndLinkSections()
    {
        var email = Email(
            subject: "Weekly plan",
            senderName: "Sacred Heart",
            body: "See attached plan",
            receivedDate: new DateTimeOffset(2026, 4, 20, 7, 0, 0, TimeSpan.Zero));

        var prompt = ClaudeSummarizerService.BuildSummarizePrompt(
            email, "Monday, 20 April 2026", "\n\nDocument Contents:\n[plan.pdf]\nMonday: PE kit", "\n\nLinks found in email:\n- Newsletter: https://x.com/n.pdf");

        Assert.Contains("Email Subject: Weekly plan", prompt);
        Assert.Contains("See attached plan", prompt);
        Assert.Contains("Monday: PE kit", prompt);
        Assert.Contains("https://x.com/n.pdf", prompt);
        Assert.Contains("2026-04-20", prompt); // email send date for relative-date resolution
    }

    [Fact]
    public void DigestPrompt_EmbedsTheDataAndTheDate()
    {
        var (system, user) = ClaudeSummarizerService.BuildDigestPrompt(
            "Wednesday, 19 August 2026",
            "- [Teacher] Reminder: bring hats",
            2,
            "- Mon 24 Aug: Outing: Zoo",
            "- Maths worksheet due Friday");

        Assert.Contains("Wednesday, 19 August 2026", system);
        Assert.Contains("TOMORROW", system);
        Assert.Contains("## TODAY'S EMAILS (2 received)", user);
        Assert.Contains("Reminder: bring hats", user);
        Assert.Contains("Outing: Zoo", user);
        Assert.Contains("Maths worksheet due Friday", user);
    }
}
