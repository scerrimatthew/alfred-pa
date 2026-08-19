using System.Text;
using System.Text.RegularExpressions;
using Alfred.Functions.Configuration;
using Alfred.Functions.Models;
using Alfred.Functions.Services.Pdf;
using Alfred.Functions.Services.State;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alfred.Functions.Services.Gmail;

public partial class GmailReaderService : IGmailReaderService
{
    private readonly AlfredOptions _alfredOptions;
    private readonly GoogleOptions _googleOptions;
    private readonly IStateService _stateService;
    private readonly IPdfExtractorService _pdfExtractor;
    private readonly ILogger<GmailReaderService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _labelIdCache = new();

    public GmailReaderService(
        IOptions<AlfredOptions> alfredOptions,
        IOptions<GoogleOptions> googleOptions,
        IStateService stateService,
        IPdfExtractorService pdfExtractor,
        ILogger<GmailReaderService> logger)
    {
        _alfredOptions = alfredOptions.Value;
        _googleOptions = googleOptions.Value;
        _stateService = stateService;
        _pdfExtractor = pdfExtractor;
        _logger = logger;
    }

    public async Task<List<SchoolEmail>> GetNewEmailsAsync()
    {
        var afterEpoch = DateTimeOffset.UtcNow.AddHours(-_alfredOptions.LookbackHours).ToUnixTimeSeconds();
        var query = $"{UnreadFilter}from:{_alfredOptions.SchoolEmailSender} after:{afterEpoch}";

        return await FetchNewEmailsAsync(
            query,
            _stateService.IsEmailProcessedAsync,
            downloadLinkedDocuments: true,
            label: "school");
    }

    public async Task<List<SchoolEmail>> GetNewPersonalEmailsAsync()
    {
        var lookbackHours = _alfredOptions.PersonalLookbackHours > 0
            ? _alfredOptions.PersonalLookbackHours
            : _alfredOptions.LookbackHours;
        var afterEpoch = DateTimeOffset.UtcNow.AddHours(-lookbackHours).ToUnixTimeSeconds();
        // Everything in the inbox except school mail; promotions/social tabs are noise not worth a Claude call
        var query = $"{UnreadFilter}in:inbox -from:{_alfredOptions.SchoolEmailSender} -category:promotions -category:social after:{afterEpoch}";

        return await FetchNewEmailsAsync(
            query,
            _stateService.IsPersonalEmailProcessedAsync,
            downloadLinkedDocuments: false,
            label: "personal");
    }

    // Date-window queries (the default) catch emails Matthew reads before the poll — the
    // ProcessedEmails table is what prevents reprocessing, not the unread flag
    private string UnreadFilter => _alfredOptions.IncludeReadEmails ? "" : "is:unread ";

    private async Task<List<SchoolEmail>> FetchNewEmailsAsync(
        string query, Func<string, Task<bool>> isProcessed, bool downloadLinkedDocuments, string label)
    {
        const int maxTotalMessages = 300;

        var gmailService = CreateGmailService();
        var newEmails = new List<SchoolEmail>();

        _logger.LogInformation("Querying Gmail: {Query}", query);

        var messageRefs = new List<Message>();
        string? pageToken = null;
        do
        {
            var request = gmailService.Users.Messages.List("me");
            request.Q = query;
            request.MaxResults = 50;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync();
            if (response.Messages is not null)
                messageRefs.AddRange(response.Messages);

            pageToken = response.NextPageToken;
        } while (pageToken is not null && messageRefs.Count < maxTotalMessages);

        if (messageRefs.Count == 0)
        {
            _logger.LogInformation("No {Label} emails found in the lookback window", label);
            return newEmails;
        }

        foreach (var messageRef in messageRefs)
        {
            if (await isProcessed(messageRef.Id))
            {
                _logger.LogDebug("Skipping already-processed email {MessageId}", messageRef.Id);
                continue;
            }

            var fullMessage = await gmailService.Users.Messages.Get("me", messageRef.Id).ExecuteAsync();
            var schoolEmail = await ParseMessageAsync(gmailService, fullMessage, downloadLinkedDocuments);

            if (schoolEmail is not null)
            {
                newEmails.Add(schoolEmail);
            }
        }

        _logger.LogInformation("Found {Count} new {Label} emails to process", newEmails.Count, label);
        newEmails.Reverse(); // Process oldest first
        return newEmails;
    }

    // One batch of a historical sweep: the OLDEST unprocessed personal emails in the
    // window. Pages the entire window's message ids (cheap) before choosing, so the
    // batch is genuinely oldest-first and already-processed mail — from earlier
    // batches, normal runs, or previous backfills — is never fetched twice.
    public async Task<List<SchoolEmail>> GetBackfillBatchAsync(DateTimeOffset oldestDate, int batchSize)
    {
        const int maxRefsToScan = 2000;

        var gmailService = CreateGmailService();
        var afterEpoch = oldestDate.ToUnixTimeSeconds();
        // Same shape as the personal monitor query, but read state is irrelevant here
        var query = $"in:inbox -from:{_alfredOptions.SchoolEmailSender} -category:promotions -category:social after:{afterEpoch}";

        _logger.LogInformation("Backfill query: {Query} (batch {Batch})", query, batchSize);

        var messageRefs = new List<Message>();
        string? pageToken = null;
        do
        {
            var request = gmailService.Users.Messages.List("me");
            request.Q = query;
            request.MaxResults = 100;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync();
            if (response.Messages is not null)
                messageRefs.AddRange(response.Messages);

            pageToken = response.NextPageToken;
        } while (pageToken is not null && messageRefs.Count < maxRefsToScan);

        // Gmail lists newest first — walk from the end so the batch is oldest-first
        messageRefs.Reverse();

        var batch = new List<SchoolEmail>();
        foreach (var messageRef in messageRefs)
        {
            if (batch.Count >= batchSize)
                break;

            if (await _stateService.IsPersonalEmailProcessedAsync(messageRef.Id))
                continue;

            var fullMessage = await gmailService.Users.Messages.Get("me", messageRef.Id).ExecuteAsync();
            var email = await ParseMessageAsync(gmailService, fullMessage, downloadLinkedDocuments: false);
            if (email is not null)
                batch.Add(email);
        }

        _logger.LogInformation("Backfill batch: {Count} unprocessed emails (scanned {Refs} refs)",
            batch.Count, messageRefs.Count);
        return batch;
    }

    // Applies the category label without touching the read flag — backfilled mail
    // keeps whatever read state Matthew left it in
    public async Task LabelWithoutMarkingReadAsync(string messageId, string labelPath)
    {
        try
        {
            var gmailService = CreateGmailService();
            var labelId = await GetOrCreateLabelIdAsync(gmailService, labelPath);

            var request = new ModifyMessageRequest { AddLabelIds = [labelId] };
            await gmailService.Users.Messages.Modify(request, "me", messageId).ExecuteAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same as MarkAsReadAndLabelAsync
            _logger.LogWarning(ex, "Failed to label {MessageId} with {Label}", messageId, labelPath);
        }
    }

    public async Task<List<InboxSearchResult>> SearchInboxAsync(string query, int maxResults)
    {
        var gmailService = CreateGmailService();
        var capped = Math.Clamp(maxResults, 1, 20);

        _logger.LogInformation("On-demand inbox search: {Query} (max {Max})", query, capped);

        var request = gmailService.Users.Messages.List("me");
        request.Q = query;
        request.MaxResults = capped;
        var response = await request.ExecuteAsync();

        var results = new List<InboxSearchResult>();
        foreach (var messageRef in (response.Messages ?? []).Take(capped))
        {
            var getRequest = gmailService.Users.Messages.Get("me", messageRef.Id);
            getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
            getRequest.MetadataHeaders = new Google.Apis.Util.Repeatable<string>(["Subject", "From", "Date"]);
            var message = await getRequest.ExecuteAsync();

            var headers = message.Payload?.Headers ?? [];
            var from = headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "";
            var dateHeader = headers.FirstOrDefault(h => h.Name == "Date")?.Value;

            results.Add(new InboxSearchResult
            {
                MessageId = message.Id,
                ThreadId = message.ThreadId ?? message.Id,
                Subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(No subject)",
                SenderName = ExtractSenderName(from),
                SenderEmail = ExtractEmail(from),
                ReceivedDate = dateHeader is not null && DateTimeOffset.TryParse(dateHeader, out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow,
                Snippet = System.Net.WebUtility.HtmlDecode(message.Snippet ?? "")
            });
        }

        _logger.LogInformation("Inbox search returned {Count} results", results.Count);
        return results;
    }

    public async Task<SchoolEmail?> GetEmailAsync(string messageId)
    {
        var gmailService = CreateGmailService();
        try
        {
            var message = await gmailService.Users.Messages.Get("me", messageId).ExecuteAsync();
            return await ParseMessageAsync(gmailService, message, downloadLinkedDocuments: false);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Email {MessageId} not found", messageId);
            return null;
        }
    }

    public async Task MarkAsReadAndLabelAsync(string messageId, string labelPath)
    {
        try
        {
            var gmailService = CreateGmailService();
            var labelId = await GetOrCreateLabelIdAsync(gmailService, labelPath);

            var request = new ModifyMessageRequest
            {
                AddLabelIds = [labelId],
                RemoveLabelIds = ["UNREAD"]
            };

            await gmailService.Users.Messages.Modify(request, "me", messageId).ExecuteAsync();
            _logger.LogInformation("Marked {MessageId} as read and labeled {Label}", messageId, labelPath);
        }
        catch (Exception ex)
        {
            // Best-effort — the ProcessedEmails table already prevents reprocessing,
            // so a failure here only leaves the email unread/unlabeled in Gmail
            _logger.LogWarning(ex, "Failed to mark {MessageId} as read / apply label {Label}", messageId, labelPath);
        }
    }

    public async Task MarkAsUnreadAsync(string messageId)
    {
        var gmailService = CreateGmailService();
        var request = new ModifyMessageRequest { AddLabelIds = ["UNREAD"] };
        await gmailService.Users.Messages.Modify(request, "me", messageId).ExecuteAsync();
        _logger.LogInformation("Marked {MessageId} as unread", messageId);
    }

    // Saves a reply as a Gmail draft in the original thread — it is NEVER sent automatically
    public async Task<string> CreateReplyDraftAsync(string messageId, string body, bool replyAll)
    {
        var gmailService = CreateGmailService();

        var getRequest = gmailService.Users.Messages.Get("me", messageId);
        getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
        getRequest.MetadataHeaders = new Google.Apis.Util.Repeatable<string>(
            ["Subject", "From", "To", "Cc", "Reply-To", "Message-ID", "References"]);
        var original = await getRequest.ExecuteAsync();

        var headers = original.Payload?.Headers ?? [];
        string? Header(string name) => headers.FirstOrDefault(
            h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        var replyTo = Header("Reply-To") ?? Header("From")
            ?? throw new InvalidOperationException("Original email has no sender to reply to.");

        var subject = Header("Subject") ?? "(no subject)";
        if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
            subject = $"Re: {subject}";

        var originalMessageId = Header("Message-ID");
        var references = Header("References");

        var mime = new StringBuilder();
        mime.Append("To: ").Append(replyTo).Append("\r\n");

        if (replyAll)
        {
            var ownAddress = await GetOwnAddressAsync(gmailService);
            var replyToEmail = ExtractEmail(replyTo);
            var ccList = SplitAddresses($"{Header("To")}, {Header("Cc")}")
                .Where(a =>
                {
                    var addr = ExtractEmail(a);
                    return !addr.Equals(ownAddress, StringComparison.OrdinalIgnoreCase)
                        && !addr.Equals(replyToEmail, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            if (ccList.Count > 0)
                mime.Append("Cc: ").Append(string.Join(", ", ccList)).Append("\r\n");
        }

        mime.Append("Subject: ").Append(EncodeHeaderValue(subject)).Append("\r\n");
        if (originalMessageId is not null)
        {
            mime.Append("In-Reply-To: ").Append(originalMessageId).Append("\r\n");
            mime.Append("References: ")
                .Append(references is not null ? $"{references} {originalMessageId}" : originalMessageId)
                .Append("\r\n");
        }
        mime.Append("MIME-Version: 1.0\r\n");
        mime.Append("Content-Type: text/plain; charset=\"UTF-8\"\r\n");
        mime.Append("Content-Transfer-Encoding: base64\r\n");
        mime.Append("\r\n");
        mime.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(body), Base64FormattingOptions.InsertLineBreaks));

        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(mime.ToString()))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var draft = await gmailService.Users.Drafts.Create(
            new Draft { Message = new Message { Raw = raw, ThreadId = original.ThreadId } }, "me").ExecuteAsync();

        _logger.LogInformation("Created reply draft {DraftId} to {To}: {Subject}", draft.Id, replyTo, subject);

        return $"Draft reply to {replyTo} saved in Gmail Drafts (subject \"{subject}\"), in the original thread: "
            + GmailLinks.ForThread(original.ThreadId ?? messageId);
    }

    // True when the thread contains a message Matthew sent after the given message —
    // i.e. he has already replied and doesn't need a nudge
    public async Task<bool> HasRepliedAsync(string threadId, string messageId)
    {
        var gmailService = CreateGmailService();
        try
        {
            var request = gmailService.Users.Threads.Get("me", threadId);
            request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Minimal;
            var thread = await request.ExecuteAsync();

            var messages = thread.Messages ?? [];
            var targetDate = messages.FirstOrDefault(m => m.Id == messageId)?.InternalDate ?? 0;

            return messages.Any(m => m.LabelIds?.Contains("SENT") == true
                && (m.InternalDate ?? 0) > targetDate);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Thread {ThreadId} not found while checking for a reply", threadId);
            return false;
        }
    }

    // Sends a bare unsubscribe email to a mailing list's mailto: unsubscribe address.
    // The ONLY place Alfred sends email, and only ever to an address the list itself
    // published in its List-Unsubscribe header.
    public async Task SendUnsubscribeEmailAsync(string toAddress, string? subject)
    {
        var gmailService = CreateGmailService();

        var mime = $"To: {toAddress}\r\nSubject: {EncodeHeaderValue(subject ?? "unsubscribe")}\r\n\r\nunsubscribe";
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(mime))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await gmailService.Users.Messages.Send(new Message { Raw = raw }, "me").ExecuteAsync();
        _logger.LogInformation("Sent unsubscribe email to {Address}", toAddress);
    }

    private string? _ownAddressCache;

    private async Task<string> GetOwnAddressAsync(GmailService gmailService)
    {
        if (_ownAddressCache is not null)
            return _ownAddressCache;

        var profile = await gmailService.Users.GetProfile("me").ExecuteAsync();
        _ownAddressCache = profile.EmailAddress ?? "";
        return _ownAddressCache;
    }

    internal static IEnumerable<string> SplitAddresses(string headerValue) =>
        headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string EncodeHeaderValue(string value) =>
        value.All(char.IsAscii)
            ? value
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";

    public async Task RecategorizeAsync(string messageId, string newLabelPath)
    {
        var gmailService = CreateGmailService();

        // Find existing Alfred-applied labels on the message so they can be swapped out:
        // bare personal category names, plus legacy Alfred/* paths
        var message = await gmailService.Users.Messages.Get("me", messageId).ExecuteAsync();
        var allLabels = await gmailService.Users.Labels.List("me").ExecuteAsync();
        var alfredLabelIds = (allLabels.Labels ?? [])
            .Where(l => l.Name.StartsWith($"{LabelNames.Root}/", StringComparison.Ordinal)
                || LabelNames.PersonalCategoryLabels.Contains(l.Name))
            .Select(l => l.Id)
            .Where(id => message.LabelIds?.Contains(id) == true)
            .ToList();

        var newLabelId = await GetOrCreateLabelIdAsync(gmailService, newLabelPath);
        var request = new ModifyMessageRequest
        {
            AddLabelIds = [newLabelId],
            RemoveLabelIds = alfredLabelIds.Where(id => id != newLabelId).ToList()
        };

        await gmailService.Users.Messages.Modify(request, "me", messageId).ExecuteAsync();
        _logger.LogInformation("Recategorized {MessageId} to {Label}", messageId, newLabelPath);
    }

    private async Task<string> GetOrCreateLabelIdAsync(GmailService gmailService, string labelPath)
    {
        if (_labelIdCache.TryGetValue(labelPath, out var cachedId))
            return cachedId;

        var existing = await gmailService.Users.Labels.List("me").ExecuteAsync();
        foreach (var label in existing.Labels ?? [])
        {
            _labelIdCache[label.Name] = label.Id;
        }

        // Create the label and any missing ancestors (Gmail nests labels by "/" only
        // when the parent label exists)
        var segments = labelPath.Split('/');
        for (var i = 1; i <= segments.Length; i++)
        {
            var path = string.Join('/', segments[..i]);
            if (_labelIdCache.ContainsKey(path))
                continue;

            var created = await gmailService.Users.Labels.Create(new Label
            {
                Name = path,
                LabelListVisibility = "labelShow",
                MessageListVisibility = "show"
            }, "me").ExecuteAsync();

            _labelIdCache[path] = created.Id;
        }

        return _labelIdCache[labelPath];
    }

    internal async Task<SchoolEmail?> ParseMessageAsync(GmailService gmailService, Message message, bool downloadLinkedDocuments)
    {
        try
        {
            var headers = message.Payload.Headers;
            var subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(No subject)";
            var from = headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "";
            var dateHeader = headers.FirstOrDefault(h => h.Name == "Date")?.Value;

            var senderName = ExtractSenderName(from);
            var receivedDate = dateHeader is not null
                ? DateTimeOffset.TryParse(dateHeader, out var parsed) ? parsed : DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow;

            var body = ExtractBody(message.Payload);

            var documents = new List<LinkedDocument>();
            documents.AddRange(await ExtractEmailAttachmentsAsync(gmailService, message));

            if (downloadLinkedDocuments)
            {
                var rawHtml = ExtractRawHtml(message.Payload);
                var linkUrls = rawHtml is not null ? ExtractLinks(rawHtml) : [];
                documents.AddRange(await DownloadLinkedDocumentsAsync(linkUrls));
            }

            return new SchoolEmail
            {
                MessageId = message.Id,
                ThreadId = message.ThreadId ?? message.Id,
                Subject = subject,
                SenderName = senderName,
                SenderEmail = ExtractEmail(from),
                ReceivedDate = receivedDate,
                Body = body,
                Documents = documents,
                WasUnread = message.LabelIds?.Contains("UNREAD") ?? true,
                ListUnsubscribe = headers.FirstOrDefault(
                    h => string.Equals(h.Name, "List-Unsubscribe", StringComparison.OrdinalIgnoreCase))?.Value,
                ListUnsubscribeOneClick = headers.Any(
                    h => string.Equals(h.Name, "List-Unsubscribe-Post", StringComparison.OrdinalIgnoreCase))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse message {MessageId}", message.Id);
            return null;
        }
    }

    private string ExtractBody(MessagePart payload)
    {
        // Try to find text/plain first
        var plainText = FindPartByMimeType(payload, "text/plain");
        if (plainText is not null)
        {
            return DecodeBase64(plainText.Body.Data);
        }

        // Fallback to text/html and strip tags
        var htmlPart = FindPartByMimeType(payload, "text/html");
        if (htmlPart is not null)
        {
            var html = DecodeBase64(htmlPart.Body.Data);
            return StripHtml(html);
        }

        // Try the body directly
        if (payload.Body?.Data is not null)
        {
            return DecodeBase64(payload.Body.Data);
        }

        return string.Empty;
    }

    private static MessagePart? FindPartByMimeType(MessagePart part, string mimeType)
    {
        if (part.MimeType == mimeType && part.Body?.Data is not null)
            return part;

        if (part.Parts is null) return null;

        foreach (var child in part.Parts)
        {
            var found = FindPartByMimeType(child, mimeType);
            if (found is not null) return found;
        }

        return null;
    }

    private async Task<List<LinkedDocument>> ExtractEmailAttachmentsAsync(GmailService gmailService, Message message)
    {
        var documents = new List<LinkedDocument>();

        if (message.Payload.Parts is null) return documents;

        foreach (var part in GetAllParts(message.Payload))
        {
            if (part.MimeType != "application/pdf" || part.Body?.AttachmentId is null)
                continue;

            try
            {
                var attachment = await gmailService.Users.Messages.Attachments
                    .Get("me", message.Id, part.Body.AttachmentId)
                    .ExecuteAsync();

                var pdfBytes = Convert.FromBase64String(
                    attachment.Data.Replace('-', '+').Replace('_', '/'));

                var extractedText = _pdfExtractor.ExtractText(pdfBytes, part.Filename ?? "attachment.pdf");

                documents.Add(new LinkedDocument
                {
                    Title = part.Filename ?? "attachment.pdf",
                    Url = $"attachment:{part.Filename}",
                    Source = LinkedDocumentSource.EmailAttachment,
                    ExtractedText = extractedText
                });

                _logger.LogInformation("Extracted text from attachment: {FileName} ({Length} chars)",
                    part.Filename, extractedText.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract PDF attachment: {FileName}", part.Filename);
            }
        }

        return documents;
    }

    private async Task<List<LinkedDocument>> DownloadLinkedDocumentsAsync(List<string> urls)
    {
        var documents = new List<LinkedDocument>();
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        foreach (var url in urls)
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var fileName = GetFileNameFromResponse(response, url);

                if (contentType == "application/pdf" || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pdfBytes = await response.Content.ReadAsByteArrayAsync();
                    var extractedText = _pdfExtractor.ExtractText(pdfBytes, fileName);

                    documents.Add(new LinkedDocument
                    {
                        Title = fileName,
                        Url = url,
                        Source = LinkedDocumentSource.BodyLink,
                        ExtractedText = extractedText
                    });

                    _logger.LogInformation("Downloaded and extracted linked PDF: {FileName} ({Length} chars)",
                        fileName, extractedText.Length);
                }
                else
                {
                    // Non-PDF link — keep the URL but no extracted text
                    documents.Add(new LinkedDocument
                    {
                        Title = fileName,
                        Url = url,
                        Source = LinkedDocumentSource.BodyLink
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download linked document: {Url}", url);
                documents.Add(new LinkedDocument
                {
                    Title = GetFileNameFromUrl(url),
                    Url = url,
                    Source = LinkedDocumentSource.BodyLink
                });
            }
        }

        return documents;
    }

    private static string GetFileNameFromResponse(HttpResponseMessage response, string url)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        if (disposition?.FileName is not null)
            return disposition.FileName.Trim('"');

        return GetFileNameFromUrl(url);
    }

    internal static string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var lastSegment = path.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "document";
            return Uri.UnescapeDataString(lastSegment);
        }
        catch
        {
            return "document";
        }
    }

    private static string? ExtractRawHtml(MessagePart payload)
    {
        var htmlPart = FindPartByMimeType(payload, "text/html");
        if (htmlPart is not null)
            return DecodeBase64(htmlPart.Body.Data);
        return null;
    }

    internal static List<string> ExtractLinks(string html)
    {
        var links = new List<string>();
        foreach (var match in HrefRegex().Matches(html).Cast<Match>())
        {
            var url = match.Groups[1].Value.Trim();
            // Skip mailto, tel, javascript, school management portal, and school Facebook
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("myschoolmanagement.com") &&
                !url.Contains("facebook.com/SacredHeart") &&
                !url.Contains("facebook.com%2FSacredHeart") &&
                !url.Contains("sacredheartcollege.msm.io%2Fannouncements"))
            {
                if (!links.Contains(url))
                    links.Add(url);
            }
        }
        return links;
    }

    private static IEnumerable<MessagePart> GetAllParts(MessagePart part)
    {
        yield return part;
        if (part.Parts is null) yield break;
        foreach (var child in part.Parts)
        {
            foreach (var descendant in GetAllParts(child))
                yield return descendant;
        }
    }

    internal static string DecodeBase64(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(base64);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static string ExtractSenderName(string from)
    {
        var match = SenderNameRegex().Match(from);
        return match.Success ? match.Groups[1].Value.Trim('"') : from;
    }

    internal static string ExtractEmail(string from)
    {
        var match = EmailRegex().Match(from);
        return match.Success ? match.Groups[1].Value : from;
    }

    internal static string StripHtml(string html)
    {
        // Remove style and script blocks entirely
        var text = StyleScriptRegex().Replace(html, "");
        // Convert block elements to newlines
        text = BlockTagRegex().Replace(text, "\n");
        // Convert <br> to newlines
        text = BrTagRegex().Replace(text, "\n");
        // Convert list items to bullet points
        text = LiTagRegex().Replace(text, "\n• ");
        // Strip remaining tags
        text = HtmlTagRegex().Replace(text, "");
        // Decode common HTML entities
        text = text.Replace("&nbsp;", " ")
                   .Replace("&amp;", "&")
                   .Replace("&lt;", "<")
                   .Replace("&gt;", ">")
                   .Replace("&quot;", "\"")
                   .Replace("&#39;", "'");
        // Clean up remaining entities
        text = HtmlEntityRegex().Replace(text, "");
        // Collapse multiple blank lines
        text = MultipleNewlinesRegex().Replace(text, "\n\n");
        // Collapse multiple spaces on same line
        text = MultipleSpacesRegex().Replace(text, " ");
        // Trim each line
        text = string.Join("\n", text.Split('\n').Select(l => l.Trim()));
        return text.Trim();
    }

    // Test seam: when set, replaces the fully-built GmailService (tests back it with
    // a fake HTTP handler). Never set in production.
    internal GmailService? GmailServiceOverride { get; set; }

    private GmailService CreateGmailService()
    {
        if (GmailServiceOverride is not null) return GmailServiceOverride;

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _googleOptions.ClientId,
                ClientSecret = _googleOptions.ClientSecret
            },
            Scopes = [GmailService.Scope.GmailModify]
        });

        var credential = new UserCredential(flow, "user", new TokenResponse
        {
            RefreshToken = _googleOptions.RefreshToken
        });

        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Alfred"
        });
    }

    [GeneratedRegex(@"^(.*?)\s*<")]
    private static partial Regex SenderNameRegex();

    [GeneratedRegex(@"<(.+?)>")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleScriptRegex();

    [GeneratedRegex(@"</(p|div|h[1-6]|tr|table)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LiTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"&\w+;")]
    private static partial Regex HtmlEntityRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
}
