using System.Text;

namespace Diagnostics;

/// <summary>Low-level markdown emit helpers for the report.</summary>
internal static class Markdown
{
    /// <summary>
    /// Appends a GitHub-flavored table with every column padded to a uniform width across the header,
    /// separator, and body — so the raw markdown source reads as aligned columns. Cell values are
    /// sanitized first: a stray pipe or newline (mod names, authors, paths are arbitrary) would
    /// otherwise split a cell into extra columns or rows.
    /// </summary>
    public static void Table(StringBuilder sb, string[] headers, List<string[]> rows)
    {
        var safeHeaders = headers.Select(Sanitize).ToArray();
        var safeRows = rows.Select(r => r.Select(Sanitize).ToArray()).ToList();

        var widths = new int[safeHeaders.Length];
        for (int c = 0; c < safeHeaders.Length; c++)
        {
            widths[c] = safeHeaders[c].Length;
        }
        foreach (var row in safeRows)
        {
            for (int c = 0; c < safeHeaders.Length; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        AppendRow(sb, safeHeaders, widths);
        AppendRow(sb, widths.Select(w => new string('-', w)).ToArray(), widths);
        foreach (var row in safeRows)
        {
            AppendRow(sb, row, widths);
        }
    }

    /// <summary>Collapses newlines to spaces and escapes pipes so a value stays inside one cell.</summary>
    private static string Sanitize(string cell) =>
        cell.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|");

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        sb.Append('|');
        for (int c = 0; c < cells.Length; c++)
        {
            sb.Append(' ').Append(cells[c].PadRight(widths[c])).Append(" |");
        }
        sb.AppendLine();
    }
}
