using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;

namespace Alfred.Functions.Models;

// Per-sender tally for the personal inbox, driving the monthly unsubscribe proposals:
// a sender whose every email was filed quietly is a candidate for unsubscribing.
public class SenderStatsEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "personal";
    public string RowKey { get; set; } = string.Empty; // hash of the lowercased sender email
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int QuietCount { get; set; } // processed without needing Matthew's attention
    public DateTimeOffset LastSeen { get; set; }
    public string? ListUnsubscribe { get; set; } // raw List-Unsubscribe header, if the sender offers one
    public bool ListUnsubscribeOneClick { get; set; } // RFC 8058 List-Unsubscribe-Post present
    public DateTimeOffset? ProposedAt { get; set; } // when Alfred suggested unsubscribing (only ever once)
    public bool Unsubscribed { get; set; }

    // Sender emails can exceed table key limits and contain forbidden characters — key by hash
    public static string RowKeyFor(string senderEmail)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(senderEmail.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
