# Alfred - Personal Assistant

Azure Functions app that monitors a school email inbox (Sacred Heart College, Malta), uses Claude AI to summarize emails, sends Telegram notifications, creates Google Calendar events, and delivers a daily evening digest.

## Architecture

- **Runtime**: .NET 8 isolated worker on Azure Functions v4 (Linux Consumption plan)
- **Region**: West Europe
- **Resource group**: `rg-matt-scerri-alfred-prod-westeu-001`

## Project Structure

```
src/Alfred.Functions/
  Functions/
    EmailMonitorFunction.cs    # Timer (every 15 min) — checks Gmail, summarizes new emails, sends alerts
    EveningDigestFunction.cs   # Timer (2 PM UTC / 4 PM CEST) — daily summary of emails + upcoming events
  Services/
    AI/                        # Claude API integration for email summarization and digest generation
    Calendar/                  # Google Calendar — creates/deduplicates school events
    Gmail/                     # Gmail API — reads and parses school emails, extracts attachments and links
    Notifications/             # Telegram Bot — sends alerts and error notifications
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
| `Alfred__TelegramChatId` | Telegram chat to send notifications to |
| `Alfred__SchoolDaysAhead` | Days ahead to include in evening digest |
| `Google__ClientId/Secret/RefreshToken` | Google OAuth credentials |
| `Anthropic__ApiKey` | Claude API key |
| `Telegram__BotToken` | Telegram bot token |

## How It Works

1. **EmailMonitor** runs every 15 minutes, queries Gmail for emails from the configured sender within the lookback window
2. Already-processed emails are skipped via `ProcessedEmails` table in Azure Table Storage (keyed by Gmail message ID)
3. New emails are sent to Claude for summarization — extracting action items, homework, and calendar events
4. Calendar events are created in Google Calendar (deduplicated via `CalendarEvents` table)
5. A Telegram notification is sent immediately for each new email
6. **EveningDigest** runs daily at 4 PM Malta time, compiling all recent emails and upcoming calendar events into a single digest message

## Deployment

Deploy to Azure using the Azure Functions Core Tools CLI:

```bash
cd src/Alfred.Functions
func azure functionapp publish func-matt-scerri-alfred-prod-westeu-001
```

Requires `azure-functions-core-tools@4` installed via npm. The `func` CLI handles building, packaging, and deploying correctly (including the `.azurefunctions` folder required by .NET isolated worker).

Do NOT use manual zip deploy — `Compress-Archive` on Windows skips dotfiles like `.azurefunctions`.
