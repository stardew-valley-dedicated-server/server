namespace Diagnostics;

/// <summary>Value-to-text formatting shared by the report sections.</summary>
internal static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        return $"{(int)span.TotalMinutes}m {span.Seconds}s";
    }

    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    public static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Tri-state form: an absent value reads as unknown rather than defaulting to "no".</summary>
    public static string YesNo(bool? value) => value is { } known ? YesNo(known) : "unknown";

    /// <summary>A wizard answer, or a placeholder when the operator skipped it.</summary>
    public static string OrBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(blank)" : value;

    /// <summary>A collected value, or a placeholder when the response didn't carry it.</summary>
    public static string OrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
