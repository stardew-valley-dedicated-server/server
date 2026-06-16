namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Parses the <c>--filter</c> / <c>FILTER=</c> value shared by the runner's discovery,
/// the xUnit execution gate, the broker prestart, and the child expected-count seed.
/// A '|' splits the value into independent substring patterns; a test matches when ANY
/// pattern is a (case-insensitive) substring of its class FullName or display name —
/// the same OR semantics xUnit's own <c>AddIncludedMethodFilter</c> collection applies.
/// </summary>
public static class TestFilter
{
    /// <summary>
    /// Splits a raw filter into its '|'-separated patterns, trimming whitespace and
    /// dropping empties. Returns an empty array for null/empty (meaning "no filter").
    /// </summary>
    public static string[] ParsePatterns(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return Array.Empty<string>();
        }

        return filter.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
    }

    /// <summary>
    /// True when no filter is active, or when any pattern matches the class FullName or
    /// the <c>{ClassFullName}.{MethodName}</c> display name (case-insensitive substring).
    /// </summary>
    public static bool Matches(string? filter, string classFullName, string displayName)
    {
        var patterns = ParsePatterns(filter);
        if (patterns.Length == 0)
        {
            return true;
        }

        foreach (var pattern in patterns)
        {
            if (
                classFullName.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || displayName.Contains(pattern, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }
}
