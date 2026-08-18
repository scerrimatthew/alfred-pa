# Alfred - Personal Assistant

Azure Functions app that monitors a Gmail inbox, uses Claude AI to summarize emails, sends Telegram notifications, creates Google Calendar events, delivers a daily evening digest, and provides a Telegram chat Q&A interface. It has two monitoring pipelines:

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
| `Alfred__TelegramWebhookSecret` | Secret token in webhook URL for Telegram bot |
| `Alfred__AllowedTelegramUserIds` | Comma-separated Telegram user IDs allowed to chat (empty = all) |
| `Alfred__ChatLookbackDays` | How far back to search emails for chat context (default: 30) |
| `Alfred__ChatHistoryMaxTurns` | Max recent Q&A turns replayed to chat for follow-up questions (default: 5) |
| `Alfred__ChatHistoryMaxAgeMinutes` | Chat turns older than this never enter the prompt (default: 60) |
| `Google__ClientId/Secret/RefreshToken` | Google OAuth credentials |
| `Anthropic__ApiKey` | Claude API key |
| `Telegram__BotToken` | Telegram bot token |

## How It Works

1. **EmailMonitor** runs every 15 minutes, queries Gmail for emails from the configured sender within the lookback window (all of them by default; only unread when `Alfred__IncludeReadEmails` is `false`)
2. Already-processed emails are skipped via `ProcessedEmails` table in Azure Table Storage (keyed by Gmail message ID)
3. New emails are sent to Claude for summarization — extracting action items, homework, calendar events, and a category
4. Calendar events are created in Google Calendar (deduplicated via `CalendarEvents` table)
5. A Telegram notification is sent immediately for each new email; the email is then marked read in Gmail and labeled `Alfred/School/<Category>` (weekly-plan, homework, event, outing, meeting, newsletter, admin, other)
6. **PersonalEmailMonitor** runs every 15 minutes (offset :05), queries the inbox for email from anyone *except* the school sender (skipping the promotions/social Gmail tabs; unread-only when `Alfred__IncludeReadEmails` is `false`), has Claude triage each email, and sends a notification to the personal Telegram chat for attention-worthy emails (invoices, payment requests, personal replies, appointments, security/official notifications). Each alert carries inline buttons — **Mark unread**, **Mute sender** (creates a suppression rule for that sender), and **Remind me tomorrow** (snoozes to 08:00 Malta) — handled as `callback_query` updates by TelegramWebhook. Snoozed emails live in the `SnoozedEmails` table; **SnoozeCheck** (timer, every 15 min at :10) re-sends the alert when a snooze falls due, with a snooze-again button. Chat tools `snooze_email` / `list_snoozes` / `cancel_snooze` allow arbitrary reminder times ("snooze the GO bill till Friday") Others are marked processed silently. Dated actions found during triage (payment deadlines, appointments) become events on the personal calendar, tagged with a private `alfred=true` extended property for later filtering. Triage is **thread-aware**: when a new message arrives in a thread Alfred already processed, the earlier summaries are passed in and the email is treated as a follow-up — summarized by what's new, not re-alerted for pleasantries or duplicate calendar events. Triage also runs a **fraud check** on anything asking for money (sender domain vs claimed organization, lookalike domains, changed-bank-details tells); a suspicious email always notifies with a "⚠️ Careful" warning prepended, overrides suppression, and never creates calendar reminders. Every processed email is marked read and labeled `Alfred/Personal/<Category>`. Personal emails use the `personal` partition in `ProcessedEmails`, so they never appear in the school digest or Q&A. The function is disabled until `Alfred__PersonalTelegramChatId` is set
7. **EveningDigest** runs daily at 4 PM Malta time and sends two independent digests: the **school digest** (school chat — recent school emails + upcoming events) and the **personal digest** (personal chat — today's personal emails + Alfred-created actions/deadlines over the next `PersonalDigestDaysAhead` days). Triage flags person-to-person emails that expect an answer (`NeedsReply` in state); the personal digest checks each flagged thread for a sent reply from Matthew (clearing the flag when found) and nudges about the still-unanswered ones from the past week. Chat context shows them as `[needs reply]`. During the summer break window (`SummerBreakStart`–`SummerBreakEnd`) the school digest is skipped; to compensate, EmailMonitor sends an immediate alert for **every** school email during the break (not just urgent ones). The personal digest runs year-round
8. **MorningReminder** runs daily at 5 AM UTC and messages the personal chat with Alfred-created calendar actions (deadlines, appointments) starting today or tomorrow (Malta time), grouped by day. Silent when nothing is due; no Claude call involved
9. **TelegramWebhook** receives messages sent to the bot and answers via Claude. In the shared school chat it loads 30 days of school email summaries plus calendar events (school scope only). In the personal DM (`chat id == PersonalTelegramChatId`) it additionally loads personal email summaries and Alfred-created personal calendar actions, and Claude can execute actions via tool use: `mark_unread`, `recategorize_email` (swaps the category label and updates state), `add/list/remove_suppression_rule`, `update/delete_calendar_event` (Alfred-created personal events only — guarded by the `alfred=true` tag), `draft_reply` (writes a reply in Matthew's voice and saves it as a Gmail draft in the original thread — never sends; Matthew reviews and sends from Gmail), plus two read-only fallback tools for emails the processed-email context doesn't cover (too old, read before Alfred saw them): `search_inbox` (raw Gmail query search, header + snippet results, capped at 20) and `read_email` (full body + PDF attachment text for one message, truncated). Claude is instructed to answer from the provided context first and only search when it can't. Access is controlled via `AllowedTelegramUserIds`. The webhook keeps short-lived **conversation memory** per chat: each answered question is saved to the `ChatHistory` table (question + de-formatted answer capped at 700 chars), and the last `ChatHistoryMaxTurns` turns from the past `ChatHistoryMaxAgeMinutes` minutes are replayed to Claude as a clearly-labeled, Malta-timestamped "recent conversation" block so follow-ups ("and what about Tuesday?") work. The prompt tells Claude the history may be unrelated and that live email/calendar data wins over old answers. Sending `/new` (or `/reset`) clears the thread; turns older than a day are pruned automatically on each save
10. **Suppression rules** ("ignore these Bolt reports in future") are stored in the `SuppressionRules` table as generalized natural-language patterns written by Claude from the user's request. Each personal triage pass receives the active rules and matches them by reasoning (a rule about the July edition of a monthly report matches the August one too). Suppressed emails are still marked read, labeled, and recorded in state (`Suppressed = true`), but never notify, never create calendar events, and are excluded from the personal digest. **Attention rules** (`AttentionRules` table, managed via `add/list/remove_attention_rule`) are the positive counterpart: emails matching one always notify, overriding both the triage bar and any suppression rule

Emails read manually before the next poll are still processed by default (`IncludeReadEmails`), just without an alert — they feed the digests, calendar, and chat context; the `ProcessedEmails` table is what prevents reprocessing. Marking read / labeling is best-effort: if it fails, the state table still prevents reprocessing. Gmail labels are created automatically on first use. Requires the `gmail.modify` OAuth scope — refresh tokens issued for the old read-only scope must be regenerated with `tools/GetGoogleRefreshToken`

## Self-modification (/evolve)

Sending `/evolve <instruction>` to the bot from the personal DM dispatches the `evolve` GitHub Actions workflow (`.github/workflows/evolve.yml`), which runs a headless Claude Code session against this repo, builds, commits, pushes, deploys to Azure, and reports back via Telegram. Requirements:

- Function app settings: `GitHub__Token` (fine-grained PAT with Actions read/write on this repo), `GitHub__Repo` (default: `scerrimatthew/alfred-pa`)
- GitHub repo secrets: `ANTHROPIC_API_KEY`, `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`, `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID`

## Deployment

Deploy to Azure using the Azure Functions Core Tools CLI:

```bash
cd src/Alfred.Functions
func azure functionapp publish func-matt-scerri-alfred-prod-westeu-001
```

Requires `azure-functions-core-tools@4` installed via npm. The `func` CLI handles building, packaging, and deploying correctly (including the `.azurefunctions` folder required by .NET isolated worker).

Do NOT use manual zip deploy — `Compress-Archive` on Windows skips dotfiles like `.azurefunctions`.

IMPORTANT: the preferred deploy path is now the `evolve` GitHub Actions workflow (or any Actions deploy using `Azure/functions-action`). A local `func` CLI deploy sets `WEBSITE_RUN_FROM_PACKAGE` to a blob URL, which breaks subsequent Actions deploys until that app setting is removed. If you deploy locally, remove `WEBSITE_RUN_FROM_PACKAGE` afterwards to keep /evolve working.

## Telegram Webhook Setup

After deploying, register the webhook with Telegram:

```
https://api.telegram.org/bot<BOT_TOKEN>/setWebhook?url=https://func-matt-scerri-alfred-prod-westeu-001.azurewebsites.net/api/telegram/<WEBHOOK_SECRET>
```
