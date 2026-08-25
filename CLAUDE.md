# Alfred - Personal Assistant

Azure Functions app that monitors a Gmail inbox, uses Claude AI to summarize emails, sends Telegram notifications, creates Google Calendar events, delivers a daily evening digest, reports weekly on the ETFs Matthew follows, and provides a Telegram chat Q&A interface. It has two monitoring pipelines:

- **School** (Sacred Heart College, Malta): summarizes school emails, creates calendar events, evening digest, Q&A — all in the school Telegram chat
- **Personal**: triages all other inbox email, notifying a separate personal Telegram chat about emails that warrant attention (invoices, payment requests, personal replies, official/security notifications)

## Architecture

- **Runtime**: .NET 8 isolated worker on Azure Functions v4 (Linux Consumption plan)
- **Region**: West Europe
- **Resource group**: `rg-matt-scerri-alfred-prod-westeu-001`

## Project Structure

```
src/Alfred.Functions/
  Functions/
    EmailMonitorFunction.cs         # Timer (every 15 min) — checks Gmail for school emails, summarizes, sends alerts
    PersonalEmailMonitorFunction.cs # Timer (every 15 min, offset :05) — triages non-school inbox email, alerts personal chat
    EveningDigestFunction.cs        # Timer (2 PM UTC / 4 PM CEST) — daily summary of school emails + upcoming events
    MorningReminderFunction.cs      # Timer (5 AM UTC) — personal-chat nudge for Alfred-created actions due today/tomorrow
    SnoozeCheckFunction.cs          # Timer (every 15 min, offset :10) — resurfaces snoozed emails when due
    MailingListHygieneFunction.cs   # Timer (1st of month) — proposes unsubscribing from never-useful senders
    AiNewsDigestFunction.cs         # Timer (6 PM UTC / 8 PM CEST) — evening AI-news briefing researched via Claude web search
    AiNewsFlashFunction.cs          # Timer (10 AM UTC / noon CEST) — midday check for flag-level AI news that can't wait for the evening
    AiNewsWeeklyFunction.cs         # Timer (Friday 4 PM UTC) — weekly "AI world vs the thesis" synthesis of the week's reported stories
    EtfWeeklyReportFunction.cs      # Timer (Saturday 8:30 AM UTC) — weekly read on the tracked ETFs: price, weekly move, and why it moved
    TelegramWebhookFunction.cs # HTTP POST — receives Telegram messages, answers questions via Claude
  Services/
    AI/                        # Claude API integration for email summarization, digest generation, and Q&A
    Calendar/                  # Google Calendar — creates/deduplicates school events
    Gmail/                     # Gmail API — reads and parses school emails, extracts attachments and links
    Notifications/             # Telegram Bot — sends alerts, error notifications, and chat replies
    Pdf/                       # PDF text extraction from email attachments and linked documents
    State/                     # Azure Table Storage — tracks processed emails and calendar event mappings
  Configuration/               # Options classes (AlfredOptions, GoogleOptions)
  Models/                      # Domain models (SchoolEmail, EmailDigest, CalendarEventInfo, etc.)
tools/
  GetGoogleRefreshToken/       # Utility to obtain Google OAuth refresh token
  TestHarness/                 # Local testing utility
tests/
  Alfred.Functions.Tests/      # Unit tests (xunit + NSubstitute + coverlet) — see "Testing & merge rules"
```

## Key App Settings

| Setting | Purpose |
|---|---|
| `Alfred__LookbackHours` | How far back to query Gmail (default: 25h) |
| `Alfred__SchoolEmailSender` | Email address to filter on |
| `Alfred__SharedCalendarId` | Google Calendar ID for school events |
| `Alfred__TelegramChatId` | Telegram chat for school notifications |
| `Alfred__PersonalTelegramChatId` | Telegram chat for personal inbox notifications (empty = personal monitor disabled) |
| `Alfred__NotifyAllPersonalEmails` | `true` = notify for every personal email, not just attention-worthy ones (default: false) |
| `Alfred__PersonalCalendarId` | Calendar for personal actions/deadlines (default: `primary`) |
| `Alfred__PersonalLookbackHours` | Override lookback for the personal monitor only; 0 = use `LookbackHours`. Set high temporarily to sweep a backlog |
| `Alfred__IncludeReadEmails` | `true` (default) = monitors query by date window, so emails read before the poll are still processed (silently, no alert). `false` = old unread-only behavior |
| `Alfred__PersonalDigestDaysAhead` | Days of upcoming personal actions in the personal digest (default: 7) |
| `Alfred__SchoolDaysAhead` | Days ahead to include in evening digest |
| `Alfred__SummerBreakStart` | Start of summer break, MM-dd inclusive, Malta time (default: 07-01) |
| `Alfred__SummerBreakEnd` | End of summer break, MM-dd inclusive (default: 09-20). Digests pause during the break; school emails alert immediately instead. Empty = no pause |
| `Alfred__AiNewsEnabled` | `false` disables the evening AI-news digest (default: true; also requires `PersonalTelegramChatId`) |
| `Alfred__AiNewsMaxItems` | Max stories per AI-news digest (default: 5) |
| `Alfred__AiNewsFlashEnabled` | `false` disables the midday flag-level news check (default: true; also requires `AiNewsEnabled`) |
| `Alfred__AiNewsWeeklyEnabled` | `false` disables the Friday weekly news synthesis (default: true; also requires `AiNewsEnabled`) |
| `Alfred__EtfReportEnabled` | `false` disables the weekly ETF report (default: true; also requires `PersonalTelegramChatId`) |
| `Alfred__EtfTickers` | Comma-separated tickers seeding the ETF watchlist (e.g. `VWCE,SXR8.DE`). Optional — Matthew normally adds funds from chat ("track VWCE") |
| `Alfred__EtfMaxHoldings` | Max ETFs covered by one report, so a run stays inside its research budget (default: 8) |
| `Alfred__TelegramWebhookSecret` | Secret token in webhook URL for Telegram bot |
| `Alfred__AllowedTelegramUserIds` | Comma-separated Telegram user IDs allowed to chat (empty = all) |
| `Alfred__ChatLookbackDays` | How far back to search emails for chat context (default: 30) |
| `Alfred__ChatHistoryMaxTurns` | Max recent Q&A turns replayed to chat for follow-up questions (default: 5) |
| `Alfred__ChatHistoryMaxAgeMinutes` | Chat turns older than this never enter the prompt (default: 60) |
| `Google__ClientId/Secret/RefreshToken` | Google OAuth credentials |
| `Anthropic__ApiKey` | Claude API key |
| `Anthropic__AdminApiKey` | Anthropic Admin API key (`sk-ant-admin…`, Console → Settings → Organization → Admin keys; needs an organization account) — powers `/cost`. Empty = `/cost` replies with setup guidance |
| `Telegram__BotToken` | Telegram bot token |

## How It Works

1. **EmailMonitor** runs every 15 minutes, queries Gmail for emails from the configured sender within the lookback window (all of them by default; only unread when `Alfred__IncludeReadEmails` is `false`)
2. Already-processed emails are skipped via `ProcessedEmails` table in Azure Table Storage (keyed by Gmail message ID)
3. New emails are sent to Claude for summarization — extracting action items, homework, calendar events, and a category
4. Calendar events are created in Google Calendar (deduplicated via the `CalendarEvents` table, then by a title-similarity scan of the target date ±1 day — the `Deadline:`/`Appointment:`/`Outing:`-style prefix is ignored and two thirds of the remaining words must match, so unrelated entries in the same window don't cancel each other out). Every event gets a popup reminder at 6 PM the evening before; when that moment has already passed (email arrives late, or the event is moved from chat) the reminder is pulled forward to a few minutes from now instead of being set in the past where it would never fire. An event that has already started gets no reminder — Google anchors reminders to the start time
5. A Telegram notification is sent immediately for each new email; the email is then marked read in Gmail and labeled `Alfred/School/<Category>` (weekly-plan, homework, event, outing, meeting, newsletter, admin, other)
6. **PersonalEmailMonitor** runs every 15 minutes (offset :05), queries the inbox for email from anyone *except* the school sender (skipping the promotions/social Gmail tabs; unread-only when `Alfred__IncludeReadEmails` is `false`), has Claude triage each email, and sends a notification to the personal Telegram chat for attention-worthy emails (invoices, payment requests, personal replies, appointments, security/official notifications). Each alert carries inline buttons — **Mark unread**, **Mute sender** (creates a suppression rule for that sender), and **Remind me tomorrow** (snoozes to 08:00 Malta) — handled as `callback_query` updates by TelegramWebhook. Snoozed emails live in the `SnoozedEmails` table; **SnoozeCheck** (timer, every 15 min at :10) re-sends the alert when a snooze falls due, with a snooze-again button. Chat tools `snooze_email` / `list_snoozes` / `cancel_snooze` allow arbitrary reminder times ("snooze the GO bill till Friday"). Others are marked processed silently. Dated actions found during triage (payment deadlines, appointments) become events on the personal calendar, tagged with a private `alfred=true` extended property for later filtering. Triage is **thread-aware**: when a new message arrives in a thread Alfred already processed, the earlier summaries are passed in and the email is treated as a follow-up — summarized by what's new, not re-alerted for pleasantries or duplicate calendar events. Triage also receives the saved **user facts** (see `remember_fact` in item 9) and uses them to judge relevance — an email that a fact shows doesn't apply to Matthew is filed quietly. Triage also runs a **fraud check** on anything asking for money (sender domain vs claimed organization, lookalike domains, changed-bank-details tells); a suspicious email always notifies with a "⚠️ Careful" warning prepended, overrides suppression, and never creates calendar reminders. Every processed email is marked read and labeled `Alfred/Personal/<Category>`. Personal emails use the `personal` partition in `ProcessedEmails`, so they never appear in the school digest or Q&A. The function is disabled until `Alfred__PersonalTelegramChatId` is set
7. **EveningDigest** runs daily at 4 PM Malta time and sends two independent digests: the **school digest** (school chat — recent school emails + upcoming events) and the **personal digest** (personal chat — today's personal emails + Alfred-created actions/deadlines over the next `PersonalDigestDaysAhead` days). Triage flags person-to-person emails that expect an answer (`NeedsReply` in state); the personal digest checks each flagged thread for a sent reply from Matthew (clearing the flag when found) and nudges about the still-unanswered ones from the past week. Chat context shows them as `[needs reply]`. During the summer break window (`SummerBreakStart`–`SummerBreakEnd`) the school digest is skipped; to compensate, EmailMonitor sends an immediate alert for **every** school email during the break (not just urgent ones). The personal digest runs year-round
8. **MorningReminder** runs daily at 5 AM UTC and messages the personal chat with Alfred-created calendar actions (deadlines, appointments) starting today or tomorrow (Malta time), grouped by day. Silent when nothing is due; no Claude call involved
9. **TelegramWebhook** receives messages sent to the bot and answers via Claude. Every incoming update is first claimed by `update_id` in the `ProcessedUpdates` table (add-if-absent, day-old claims pruned); Telegram re-delivers updates it thinks failed — long web-search answers and `/ai-news` runs exceed its patience — and duplicates are dropped instead of double-answered. In the shared school chat it loads 30 days of school email summaries plus calendar events (school scope only). In the personal DM (`chat id == PersonalTelegramChatId`) it additionally loads personal email summaries and Alfred-created personal calendar actions, and Claude can execute actions via tool use: `mark_unread`, `recategorize_email` (swaps the category label and updates state), `add/list/remove_suppression_rule`, `add/list/remove_attention_rule`, `add/list/remove_news_rule` (standing preferences for the AI news digest), `remember_fact`/`list_facts`/`forget_fact` (durable facts about Matthew — "my apartment at Hillcrest is A5 in Block A" — stored in the `UserFacts` table; injected into the personal chat prompt and every personal triage run, so e.g. a call for Block B owners is filed quietly instead of nagging him; chat Claude saves facts when Matthew states something worth keeping, and supersedes rather than stacks them), `snooze_email`/`list_snoozes`/`cancel_snooze`, `add_etf`/`list_etfs`/`remove_etf` (the weekly ETF watchlist — see item 14), `create_calendar_event` (adds a new reminder to the personal calendar, tagged `alfred=true`, all-day unless a time is given, with the 6 PM-evening-before popup), `update/delete_calendar_event` (Alfred-created personal events only — guarded by the `alfred=true` tag), `draft_reply` (writes a reply in Matthew's voice and saves it as a Gmail draft in the original thread — never sends; Matthew reviews and sends from Gmail), plus two read-only fallback tools for emails the processed-email context doesn't cover (too old, read before Alfred saw them): `search_inbox` (raw Gmail query search, header + snippet results, capped at 20) and `read_email` (full body + PDF attachment text for one message, truncated). Claude is instructed to answer from the provided context first and only search when it can't. The personal DM also carries the recent AI-news stories and the server-side web search tool for follow-ups (see item 12), plus the `/ai-news [topic]`, `/etf [tickers]`, `/deploy`, and `/cost` commands (`/cost` reports today/yesterday/7-day/30-day Anthropic API spend in UTC days via the Admin API cost report — billed spend only; the API doesn't expose the remaining credit balance). Access is controlled via `AllowedTelegramUserIds`. The webhook keeps short-lived **conversation memory** per chat: each answered question is saved to the `ChatHistory` table (question + de-formatted answer capped at 700 chars), and the last `ChatHistoryMaxTurns` turns from the past `ChatHistoryMaxAgeMinutes` minutes are replayed to Claude as a clearly-labeled, Malta-timestamped "recent conversation" block so follow-ups ("and what about Tuesday?") work. The prompt tells Claude the history may be unrelated and that live email/calendar data wins over old answers. Sending `/new` (or `/reset`) clears the thread; turns older than a day are pruned automatically on each save. `/joke [topic]` (either chat, no context loaded) asks Claude for a single short, family-friendly joke — optionally about a topic (`/joke about Mondays`); the last day of joke turns in that chat's history is passed back so he doesn't repeat himself, and the joke is saved as a chat turn like any other answer
10. **Backfill** (`/backfill [days]` from the personal DM, default 60, max 365; also `status` / `cancel`): quietly sweeps historical inbox email. A single-row marker in the `BackfillState` table keeps the window and running count; each PersonalEmailMonitor run processes one batch of 20 (oldest first, after fresh mail) until the window is covered, then sends a single completion message. Quiet by design: no notifications, no `NeedsReply` flags, labels applied **without** changing read state, and `ProcessedAt` backdated to the receive date so historical mail never pollutes "today's" digest or the needs-reply window. Calendar events are still created (the triage prompt drops already-passed dates) and sender tallies recorded. The `ProcessedEmails` table dedups everything, so overlapping backfills or overlap with normal runs never process an email twice
11. **Mailing-list hygiene**: every processed personal email updates a per-sender tally in the `SenderStats` table (total vs quietly-filed counts, plus the `List-Unsubscribe` header when present). **MailingListHygiene** (timer, 1st of month 14:45 UTC) proposes up to 5 senders whose every email (3+) was filed quietly, each with **Unsubscribe** / **Keep them** buttons — proposed once, never re-nagged. Unsubscribe tries RFC 8058 one-click POST first, then a mailto unsubscribe email sent via Gmail (the only email Alfred ever sends, and only to an address the list itself published), and otherwise hands Matthew the link to tap
12. **AI news digest**: **AiNewsDigest** (timer, daily 18:00 UTC / 8 PM CEST) sends the personal chat an evening AI-news briefing. `ClaudeNewsResearchService` calls Claude (`claude-opus-5`) with the Anthropic **server-side web search tool** (max 12 searches, `pause_turn` resumed up to 5 times); the prompt embeds the Cleverbit watchlist brief (`Services/AI/AiNewsBriefing.cs` — a mirror of "AI News Watchlist — PA Agent Brief" on Matthew's OneDrive; update the class when the source doc is revised). Claude ranks the last ~24-48h of news by relevance to the watchlist (max `AiNewsMaxItems` stories, PA-voice format: linked headline + one-line summary + "why it matters to us"; disconfirming thesis evidence flagged first, TL material tagged). Quiet days send nothing. Reported stories land in the `ReportedNews` table (keyed by URL hash, with the summary/why-it-matters stored for follow-ups; 14-day dedup window fed back into the prompt, pruned after 60 days) so nothing repeats unless the implication changed. Matthew steers coverage from chat — "stop covering funding rounds", "more on EU AI Act" — via `add_news_rule` / `list_news_rules` / `remove_news_rule` tools backed by the `NewsRules` table; active rules are injected into every research prompt and override the brief where they conflict. Disabled via `Alfred__AiNewsEnabled=false` or an empty `PersonalTelegramChatId`. Around the daily digest:
    - **Midday flash check**: **AiNewsFlash** (timer, daily 10:00 UTC / noon CEST, so urgency is daily-capped by construction) runs a lightweight sweep (max 6 searches) for the brief's flag-level cases only — a competitor launching into the A-SDLC niche, an Anthropic partner-program change with a deadline, thesis-level disconfirming evidence, or regulation with a compliance clock. Nearly every run finds nothing and stays silent; a hit sends a "🚨" alert immediately and records the stories in `ReportedNews` so the evening digest doesn't repeat them. Disabled via `Alfred__AiNewsFlashEnabled=false`
    - **Feedback buttons**: every digest/flash/on-demand briefing carries one 👍/👎 button pair per story. A press upserts a `NewsRules` entry keyed `fb-<urlhash>` ("More stories like …" / "Fewer stories like … — drop this topic"), so a second press or a change of heart replaces the rule instead of stacking contradictions
    - **Weekly synthesis**: **AiNewsWeekly** (timer, Friday 16:00 UTC — lands before that evening's digest) has Claude connect the week's `ReportedNews` items per vision strand ("the week in AI vs the thesis"; disconfirming signals first, closing with at most two EOS-discussion-worthy points). No web search — pure synthesis. Skipped when the week reported nothing. Disabled via `Alfred__AiNewsWeeklyEnabled=false`
    - **Newsletter mining**: personal triage extracts `newsLeads` (headline/url/note) from AI-industry newsletters passing through the inbox; the monitor saves them to the `NewsCandidates` table (keyed by URL hash, pruned after 7 days). The evening and on-demand research runs receive the last ~26h of candidates as unverified leads to check — catching stories web search misses. Suppressed (muted) newsletters are still mined — a mute governs alerts, not mining. Backfill batches don't mine (stale leads)
    - **On-demand /ai-news**: `/ai-news` from the personal DM triggers the research immediately; `/ai-news <topic>` makes it a targeted sweep (topic focus beats watchlist breadth, window may extend past 48h). `/ai_news` (the underscore form, the only one Telegram's command menu can register) works identically; the old `/news` name replies with a pointer to the new one. Sends an "on it" ack, then the briefing — and unlike the timer, a quiet result still gets a reply. A single-row `NewsRequests` marker (10-min timeout) guards against concurrent runs; an `/ai-news` sent while one is in flight gets a "still working on it" reply
    - **Time budget**: every research run is wall-clock-capped at 7.5 min (`ClaudeNewsResearchService.ResearchBudget`) via a cancellation token, with the shared research `HttpClient` at a 9-min timeout — the SDK's default 100-second `HttpClient` would abort any real multi-search run, and an unbudgeted run would blow the 10-min `functionTimeout` and die with no catch block. A cut-off run reports `Incomplete`: the evening digest sends an error ping, the flash check stays silent (waits for the evening), and `/ai-news` tells Matthew to retry
    - **Chat follow-ups**: the personal-DM Q&A context includes the last 7 days of reported stories, and chat Claude carries the server-side web search tool (max 5 searches) — "tell me more about that DORA story" pulls the primary source and gives a proper read-out. Search is reserved for news follow-ups and explicit look-it-up requests
13. **Suppression rules** ("ignore these Bolt reports in future") are stored in the `SuppressionRules` table as generalized natural-language patterns written by Claude from the user's request. Each personal triage pass receives the active rules and matches them by reasoning (a rule about the July edition of a monthly report matches the August one too). Suppressed emails are still marked read, labeled, and recorded in state (`Suppressed = true`), but never notify, never create calendar events, and are excluded from the personal digest. **Attention rules** (`AttentionRules` table, managed via `add/list/remove_attention_rule`) are the positive counterpart: emails matching one always notify, overriding both the triage bar and any suppression rule
14. **Weekly ETF report**: **EtfWeeklyReport** (timer, Saturday 08:30 UTC / 10:30 CEST — after Friday's US close, so the whole trading week is in the numbers) sends the personal chat a read on the ETFs Matthew follows: for each fund the latest close, the week's move and YTD, plus a couple of sentences on *what drove it* (the index behind it, rate decisions, earnings, oil, FX). `ClaudeEtfResearchService` calls Claude (`claude-opus-5`) with the Anthropic **server-side web search tool**, sharing the pause_turn/budget plumbing with the news research (`Services/AI/WebResearchRunner.cs` — 7.5-min wall-clock cap; a cut-off run reports `Incomplete` and pings instead of going silent). The prompt forbids buy/sell advice and price predictions — it describes, it doesn't advise — and requires a null figure over a guessed one. The watchlist lives in the `EtfHoldings` table, managed from chat via `add_etf` / `list_etfs` / `remove_etf` ("track VWCE", "stop following IWDA"), optionally seeded from `Alfred__EtfTickers` (config-seeded tickers are never written to the table, so only chat-added funds accumulate history); each chat-added fund keeps the last reported quote/move so the next report frames the week as continuation or reversal. Search budget scales with the watchlist, capped by `Alfred__EtfMaxHoldings` (overflow is stated in the message, never silently dropped). On demand, `/etf` (or `/etfs`) from the personal DM runs it immediately and `/etf VWCE, IWDA` narrows it to those tickers; a single-row marker in `NewsRequests` (`etf-request`, 10-min timeout) is taken by both the timer and `/etf`, so two runs never overlap (a `/etf` that has already finished doesn't stop that morning's timer run). When nothing is tracked, the first Saturday run asks once which funds to follow (claimed via a one-shot `meta`-partition row in `EtfHoldings`, handed back if the message fails to send) and every run after that stays silent. Disabled via `Alfred__EtfReportEnabled=false` or an empty `PersonalTelegramChatId`

Telegram messages go out in HTML mode; when Telegram rejects the markup (a stray `<`, `>` or `&` in model-written prose), `TelegramNotificationService` resends the same message once as plain text — only Telegram's own tags are stripped, so the stray character that caused the rejection survives, and links are flattened to `label (url)` — so a briefing is never lost to a punctuation mark.

Emails read manually before the next poll are still processed by default (`IncludeReadEmails`), just without an alert — they feed the digests, calendar, and chat context; the `ProcessedEmails` table is what prevents reprocessing. Marking read / labeling is best-effort: if it fails, the state table still prevents reprocessing. Gmail labels are created automatically on first use. Requires the `gmail.modify` OAuth scope — refresh tokens issued for the old read-only scope must be regenerated with `tools/GetGoogleRefreshToken`

## Self-modification (/evolve)

Sending `/evolve <instruction>` to the bot from the personal DM dispatches the `evolve` GitHub Actions workflow (`.github/workflows/evolve.yml`), which runs a headless Claude Code session against this repo, builds, commits, pushes, deploys to Azure, and reports back via Telegram. The session's progress streams live into the Actions log (stream-json rendered by `.github/scripts/evolve-stream-filter.jq`) — feature-sized instructions legitimately take 30-60+ minutes, so a quiet-looking run is usually just working. The coding step times out at 90 minutes (which triggers the failure notification), and cancelling an in-progress run notifies Telegram too (a run cancelled while still queued behind another evolve run stays silent — its job never starts). Requirements:

- Function app settings: `GitHub__Token` (fine-grained PAT with Actions read/write on this repo), `GitHub__Repo` (default: `scerrimatthew/alfred-pa`)
- GitHub repo secrets: `ANTHROPIC_API_KEY`, `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`, `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID`

## Testing & merge rules (orchestrator policy)

Unit tests live in `tests/Alfred.Functions.Tests` (xunit + NSubstitute + coverlet). These rules bind every session working on this repo, including headless `/evolve` runs.

**Separation of duties** — the following roles must be played by *separate agents* (subagents in an interactive session; the orchestrating session dispatches them and never blurs the roles):

- **Coding agent** (primary): changes production code (`src/`, `tools/`, workflows). MUST NOT create, edit, or delete anything under `tests/`, and MUST NOT weaken the coverage gate. "The coverage gate" means the threshold/filter properties in the test `.csproj` **and** the enforcement steps in `.github/workflows/ci.yml` and `evolve.yml` — removing, skipping, or loosening any of them, by any mechanism, counts as touching the gate.
- **Test-writer agent** (`.claude/agents/test-writer.md`): the only agent allowed to write under `tests/`. Never changes production code — if it needs a testability seam it reports back and the coding agent provides it.
- **Adversarial reviewer** (`.claude/agents/adversarial-reviewer.md`): reviews every change set *before commit*, read-only, explicitly hunting for bugs and for rule violations (coding agent touching tests, threshold lowered, tests gamed). Findings must be fixed by the appropriate agent — or explicitly waived by Matthew — before the change lands.

**Workflow for any change**: coding agent implements → if behavior changed or new logic needs coverage, dispatch the test-writer → run the coverage gate → adversarial review → commit only when the gate is green and the review verdict is APPROVE.

**Coverage gate** — must pass before any commit, merge, or deploy:

```powershell
# Local (the system dotnet is .NET 10; use the local 8.0 SDK)
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test tests/Alfred.Functions.Tests/Alfred.Functions.Tests.csproj /p:CollectCoverage=true
```

The line-coverage threshold lives in the test `.csproj` (`<Threshold>`). It is enforced in CI (`.github/workflows/ci.yml`, on push and PR) and in the `evolve` pipeline before commit/deploy. The threshold may be raised, never lowered, and coverage filters never loosened — except on Matthew's explicit instruction.

## Deployment

Deploy to Azure using the Azure Functions Core Tools CLI:

```bash
cd src/Alfred.Functions
func azure functionapp publish func-matt-scerri-alfred-prod-westeu-001
```

Requires `azure-functions-core-tools@4` installed via npm. The `func` CLI handles building, packaging, and deploying correctly (including the `.azurefunctions` folder required by .NET isolated worker).

Do NOT use manual zip deploy — `Compress-Archive` on Windows skips dotfiles like `.azurefunctions`.

IMPORTANT: for a plain deploy of current main (no code change), dispatch the `deploy` GitHub Actions workflow (`.github/workflows/deploy.yml`) — it builds, runs the coverage gate, deploys, and reports to Telegram. Three ways to trigger it: send `/deploy` to the bot from the personal DM; the Actions tab → deploy → Run workflow; or POST to `/repos/<repo>/actions/workflows/deploy.yml/dispatches` with a GitHub token (this is how Claude Code sessions deploy after pushing). The `evolve` workflow also deploys after its coding session. Both use `Azure/functions-action`. A local `func` CLI deploy sets `WEBSITE_RUN_FROM_PACKAGE` to a blob URL, which breaks subsequent Actions deploys until that app setting is removed. If you deploy locally, remove `WEBSITE_RUN_FROM_PACKAGE` afterwards to keep /evolve working.

## Telegram Webhook Setup

After deploying, register the webhook with Telegram:

```
https://api.telegram.org/bot<BOT_TOKEN>/setWebhook?url=https://func-matt-scerri-alfred-prod-westeu-001.azurewebsites.net/api/telegram/<WEBHOOK_SECRET>
```
