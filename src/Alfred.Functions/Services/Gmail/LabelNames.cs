namespace Alfred.Functions.Services.Gmail;

public static class LabelNames
{
    public const string Root = "Alfred";

    public static string ForSchool(string categorySlug) => $"{Root}/School/{Humanize(categorySlug)}";

    // Personal labels are bare category names — short chips in the Gmail message list
    public static string ForPersonal(string categorySlug) => Humanize(categorySlug);

    // All personal category label names; used to find the old label when recategorizing
    public static readonly HashSet<string> PersonalCategoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoice", "Payment Request", "Personal Reply", "Appointment", "Financial",
        "Official", "Security", "Delivery", "Notification", "Other"
    };

    // "payment-request" -> "Payment Request"
    private static string Humanize(string slug)
    {
        var words = slug.Trim().Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return string.IsNullOrEmpty(name) ? "Other" : name;
    }
}
