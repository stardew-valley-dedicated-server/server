using System.Text;

namespace Diagnostics;

/// <summary>Low-level markdown emit helpers for the report.</summary>
internal static class Markdown
{
    /// <summary>
    /// Appends a GitHub-flavored table with every column padded to a uniform width across the header,
    /// separator, and body — so the raw markdown source reads as aligned columns.
    /// </summary>
    public static void Table(StringBuilder sb, string[] headers, List<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
        }
        foreach (var row in rows)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        AppendRow(sb, headers, widths);
        AppendRow(sb, widths.Select(w => new string('-', w)).ToArray(), widths);
        foreach (var row in rows)
        {
            AppendRow(sb, row, widths);
        }
    }

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
