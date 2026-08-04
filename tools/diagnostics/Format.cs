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
    public static string YesNo(bool? value) => value is { } known ? YesNo(known) : Unknown;

    /// <summary>A wizard answer, or a placeholder when the operator skipped it.</summary>
    public static string OrBlank(string? value) => string.IsNullOrWhiteSpace(value) ? Blank : value;

    /// <summary>A collected value, or a placeholder when the response didn't carry it.</summary>
    public static string OrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unknown : value;

    // Placeholders are parenthesized lowercase so a cell holding no value can never be misread as
    // one, and so the reader learns a single vocabulary instead of one phrasing per section.

    /// <summary>Expected a value; the source didn't carry it.</summary>
    public const string Unknown = "(unknown)";

    /// <summary>The operator skipped an optional question.</summary>
    public const string Blank = "(blank)";

    /// <summary>An environment variable that is absent or empty.</summary>
    public const string NotSet = "(not set)";

    /// <summary>A secret that is set; the value is withheld. Pairs with <see cref="NotSet"/>.</summary>
    public const string Redacted = "(redacted)";

    /// <summary>A probe that couldn't answer here — unsupported, or it failed.</summary>
    public const string NotAvailable = "n/a";

    /// <summary>A table cell with nothing in it by design, not for lack of data.</summary>
    public const string Nothing = "-";
}
