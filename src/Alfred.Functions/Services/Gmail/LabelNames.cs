namespace Alfred.Functions.Services.Gmail;

public static class LabelNames
{
    public const string Root = "Alfred";

    public static string ForSchool(string categorySlug) => $"{Root}/School/{Humanize(categorySlug)}";

    public static string ForPersonal(string categorySlug) => $"{Root}/Personal/{Humanize(categorySlug)}";

    // "payment-request" -> "Payment Request"
    private static string Humanize(string slug)
    {
        var words = slug.Trim().Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(' ', words);
        return string.IsNullOrEmpty(name) ? "Other" : name;
    }
}
