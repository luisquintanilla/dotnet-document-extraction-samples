using System.Net;
using System.Text;

namespace OcriaNano.DocumentExtraction.Document;

/// <summary>A table as text for Markdown: cells with inclusive row/column ranges and whether they're in the header band.</summary>
internal sealed record MarkdownTable(int Rows, int Columns, IReadOnlyList<MarkdownCell> Cells);

/// <summary>One table cell for <see cref="MarkdownWriter"/>.</summary>
internal sealed record MarkdownCell(int Row, int Column, int RowSpan, int ColumnSpan, string Text, bool Header);

/// <summary>Writes page content as Markdown: headings, list items, GFM tables, and HTML tables when cells span.</summary>
internal static class MarkdownWriter
{
    private static readonly char[] Bullets = ['•', '◦', '▪', '‣', '●', '○', '■', '□', '–', '-', '*', '·'];

    /// <summary>Markdown for a text block with the given role.</summary>
    public static string Text(string role, string text, int? headingLevel)
    {
        text = text.Trim();
        return role switch
        {
            "title" or "heading" => new string('#', Math.Clamp(headingLevel ?? 2, 1, 6)) + " " + text,
            "listItem" => "- " + text.TrimStart(Bullets).TrimStart(),
            "code" => "```\n" + text + "\n```",
            "formula" => "$$\n" + text + "\n$$",
            _ => text,
        };
    }

    /// <summary>A GFM table, or an HTML table when any cell spans more than one row or column.</summary>
    public static string Table(MarkdownTable table)
    {
        if (table.Rows == 0 || table.Columns == 0)
        {
            return "";
        }

        return table.Cells.Any(c => c.RowSpan > 1 || c.ColumnSpan > 1) ? Html(table) : Gfm(table);
    }

    private static string Gfm(MarkdownTable t)
    {
        string[,] grid = new string[t.Rows, t.Columns];
        foreach (MarkdownCell c in t.Cells)
        {
            grid[c.Row, c.Column] = c.Text;
        }

        StringBuilder sb = new();
        for (int r = 0; r < t.Rows; r++)
        {
            sb.Append('|');
            for (int c = 0; c < t.Columns; c++)
            {
                sb.Append(' ').Append(Escape(grid[r, c] ?? "")).Append(" |");
            }

            sb.Append('\n');
            if (r == 0)
            {
                sb.Append('|');
                for (int c = 0; c < t.Columns; c++)
                {
                    sb.Append(" --- |");
                }

                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string Html(MarkdownTable t)
    {
        StringBuilder sb = new("<table>\n");
        foreach (IGrouping<int, MarkdownCell> row in t.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            sb.Append("<tr>");
            foreach (MarkdownCell c in row.OrderBy(c => c.Column))
            {
                string tag = c.Header ? "th" : "td";
                sb.Append('<').Append(tag);
                if (c.RowSpan > 1)
                {
                    sb.Append(" rowspan=\"").Append(c.RowSpan).Append('"');
                }

                if (c.ColumnSpan > 1)
                {
                    sb.Append(" colspan=\"").Append(c.ColumnSpan).Append('"');
                }

                sb.Append('>').Append(WebUtility.HtmlEncode(c.Text)).Append("</").Append(tag).Append('>');
            }

            sb.Append("</tr>\n");
        }

        return sb.Append("</table>").ToString();
    }

    private static string Escape(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
}
