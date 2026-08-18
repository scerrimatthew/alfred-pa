using System.Reflection;

namespace Alfred.Functions.Tests.Support;

// The SDK-wrapper services (GoogleCalendarService, ClaudeSummarizerService,
// GmailReaderService, TelegramNotificationService) keep their pure helpers private
// rather than internal, so InternalsVisibleTo alone cannot reach them. These helpers
// invoke them via reflection so their real, observable behavior (dedup similarity,
// HTML stripping, Claude response parsing, message chunking) can still be pinned.
// If a helper is renamed the lookup fails loudly with a descriptive message.
internal static class PrivateAccess
{
    public static object? Invoke(Type type, string methodName, object? instance, params object?[] args)
    {
        var candidates = type
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            .ToList();

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one method '{methodName}' with {args.Length} parameter(s) on {type.Name}, found {candidates.Count}. " +
                "Production code may have been renamed — update the test.");
        }

        try
        {
            return candidates[0].Invoke(instance, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    public static T Invoke<T>(Type type, string methodName, object? instance, params object?[] args) =>
        (T)Invoke(type, methodName, instance, args)!;
}
