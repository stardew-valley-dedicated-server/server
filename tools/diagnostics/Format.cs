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

    /// <summary>Free-text field for the report: the value, or an italic placeholder when empty.</summary>
    public static string BlankOr(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "_(blank)_" : value;
}
