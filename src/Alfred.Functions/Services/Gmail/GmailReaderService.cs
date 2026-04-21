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
        var gmailService = CreateGmailService();
        var newEmails = new List<SchoolEmail>();

        var afterEpoch = DateTimeOffset.UtcNow.AddHours(-_alfredOptions.LookbackHours).ToUnixTimeSeconds();
        var query = $"from:{_alfredOptions.SchoolEmailSender} after:{afterEpoch}";

        _logger.LogInformation("Querying Gmail: {Query}", query);

        var request = gmailService.Users.Messages.List("me");
        request.Q = query;
        request.MaxResults = 50;

        var response = await request.ExecuteAsync();

        if (response.Messages is null || response.Messages.Count == 0)
        {
            _logger.LogInformation("No school emails found in the lookback window");
            return newEmails;
        }

        foreach (var messageRef in response.Messages)
        {
            if (await _stateService.IsEmailProcessedAsync(messageRef.Id))
            {
                _logger.LogDebug("Skipping already-processed email {MessageId}", messageRef.Id);
                continue;
            }

            var fullMessage = await gmailService.Users.Messages.Get("me", messageRef.Id).ExecuteAsync();
            var schoolEmail = await ParseMessageAsync(gmailService, fullMessage);

            if (schoolEmail is not null)
            {
                newEmails.Add(schoolEmail);
            }
        }

        _logger.LogInformation("Found {Count} new school emails to process", newEmails.Count);
        return newEmails;
    }

    private async Task<SchoolEmail?> ParseMessageAsync(GmailService gmailService, Message message)
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

            var rawHtml = ExtractRawHtml(message.Payload);
            var links = rawHtml is not null ? ExtractLinks(rawHtml) : [];
            var body = ExtractBody(message.Payload);
            var pdfAttachments = await ExtractPdfAttachmentsAsync(gmailService, message);

            return new SchoolEmail
            {
                MessageId = message.Id,
                Subject = subject,
                SenderName = senderName,
                SenderEmail = ExtractEmail(from),
                ReceivedDate = receivedDate,
                Body = body,
                PdfAttachments = pdfAttachments,
                Links = links
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

    private async Task<List<PdfAttachment>> ExtractPdfAttachmentsAsync(GmailService gmailService, Message message)
    {
        var attachments = new List<PdfAttachment>();

        if (message.Payload.Parts is null) return attachments;

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

                attachments.Add(new PdfAttachment
                {
                    FileName = part.Filename ?? "attachment.pdf",
                    ExtractedText = extractedText
                });

                _logger.LogInformation("Extracted text from PDF: {FileName} ({Length} chars)",
                    part.Filename, extractedText.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract PDF attachment: {FileName}", part.Filename);
            }
        }

        return attachments;
    }

    private static string? ExtractRawHtml(MessagePart payload)
    {
        var htmlPart = FindPartByMimeType(payload, "text/html");
        if (htmlPart is not null)
            return DecodeBase64(htmlPart.Body.Data);
        return null;
    }

    private static List<string> ExtractLinks(string html)
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

    private static string DecodeBase64(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(base64);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ExtractSenderName(string from)
    {
        var match = SenderNameRegex().Match(from);
        return match.Success ? match.Groups[1].Value.Trim('"') : from;
    }

    private static string ExtractEmail(string from)
    {
        var match = EmailRegex().Match(from);
        return match.Success ? match.Groups[1].Value : from;
    }

    private static string StripHtml(string html)
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

    private GmailService CreateGmailService()
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _googleOptions.ClientId,
                ClientSecret = _googleOptions.ClientSecret
            },
            Scopes = [GmailService.Scope.GmailReadonly]
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
