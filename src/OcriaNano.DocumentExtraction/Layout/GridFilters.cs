// Ported from LiteParse crates/liteparse/src/table_model/mod.rs (Grid::is_toc, is_prose, is_chart), Apache-2.0.
using System.Globalization;
using System.Text;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>Checks that reject a decoded grid that is not really a table.</summary>
internal static class GridFilters
{
    private static int Words(string t) => t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>A table of contents: at most three columns, and most rows end in a page number (arabic or roman) with the arabic ones non-decreasing.</summary>
    public static bool IsToc(this DecodedGrid grid)
    {
        if (grid.NCols > 3)
        {
            return false;
        }

        List<(int Col, string Text)>[] rows = [.. Enumerable.Range(0, grid.NRows).Select(_ => new List<(int, string)>())];
        foreach (PlacedCell c in grid.Cells)
        {
            string t = c.Text.Trim();
            if (t.Length != 0)
            {
                rows[c.R0].Add((c.C0, t));
            }
        }

        List<List<(int Col, string Text)>> nonEmpty = [.. rows.Where(r => r.Count != 0)];
        int hits = 0;
        List<uint> arabic = [];
        foreach (List<(int Col, string Text)> r in nonEmpty)
        {
            (int Col, string Text) lastCell = r[0];
            foreach ((int Col, string Text) x in r)
            {
                if (x.Col >= lastCell.Col)
                {
                    lastCell = x;
                }
            }

            string last = lastCell.Text;
            int bytes = Encoding.UTF8.GetByteCount(last);
            bool roman = bytes <= 7 && last.All(ch => "ivxlcdmIVXLCDM".Contains(ch));
            bool num = bytes <= 4 && last.All(ch => ch is >= '0' and <= '9');
            if (r.Count >= 2 && (roman || num))
            {
                hits++;
                if (num)
                {
                    arabic.Add(uint.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out uint v) ? v : 0);
                }
            }
        }

        int falls = 0;
        for (int i = 1; i < arabic.Count; i++)
        {
            if (arabic[i] < arabic[i - 1])
            {
                falls++;
            }
        }

        bool rising = falls <= arabic.Count / 10;
        return hits >= 3 && hits * 5 >= nonEmpty.Count * 3 && rising;
    }

    /// <summary>Multi-column prose read as a table: long, mostly non-numeric cells whose text runs on down each column.</summary>
    public static bool IsProse(this DecodedGrid grid)
    {
        List<PlacedCell> cells = [.. grid.Cells.Where(c => c.Text.Trim().Length != 0)];
        if (cells.Count < 6)
        {
            return false;
        }

        List<int> wc = [.. cells.Select(c => Words(c.Text))];
        wc.Sort();
        if (wc[wc.Count / 2] < 4)
        {
            return false;
        }

        int numeric = cells.Count(c => Words(c.Text) <= 3 && c.Text.Any(ch => ch is >= '0' and <= '9'));
        if (numeric * 10 >= cells.Count * 3)
        {
            return false;
        }

        SortedDictionary<int, List<PlacedCell>> cols = [];
        foreach (PlacedCell c in cells)
        {
            if (c.C0 == c.C1)
            {
                if (!cols.TryGetValue(c.C0, out List<PlacedCell>? list))
                {
                    cols[c.C0] = list = [];
                }

                list.Add(c);
            }
        }

        int pairs = 0, flow = 0;
        foreach (List<PlacedCell> col in cols.Values)
        {
            Units.StableSort(col, (a, b) => a.R0.CompareTo(b.R0));
            for (int i = 1; i < col.Count; i++)
            {
                PlacedCell w0 = col[i - 1], w1 = col[i];
                if (w1.R0 > w0.R1 + 2)
                {
                    continue;
                }

                pairs++;
                string up = w0.Text.TrimEnd();
                bool open = up.Length == 0 || !".!?:;".Contains(up[^1]);
                string low = w1.Text.TrimStart();
                bool lower = low.Length != 0 && char.IsLower(low[0]);
                if (open && lower)
                {
                    flow++;
                }
            }
        }

        return pairs >= 3 && flow * 10 >= pairs * 4;
    }

    private static double? ParseNumber(string text)
    {
        string t = text.Trim().TrimEnd('%').Replace(",", "");
        if (t.Length == 0 || !t.All(ch => ch is >= '0' and <= '9' or '.' or '-' or '+'))
        {
            return null;
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    private static bool Stepped(List<double> vs)
    {
        if (vs.Count < 3)
        {
            return false;
        }

        double[] d = new double[vs.Count - 1];
        for (int i = 0; i < d.Length; i++)
        {
            d[i] = vs[i + 1] - vs[i];
        }

        return d[0] != 0.0 && d.All(x => Math.Abs(x - d[0]) <= 1e-6 * Math.Max(Math.Abs(d[0]), 1.0));
    }

    /// <summary>A chart read as a table: a sparse, mostly numeric grid with an axis (three or more lone numbers in an evenly stepped run).</summary>
    public static bool IsChart(this DecodedGrid grid)
    {
        int slots = grid.NRows * grid.NCols;
        if (slots == 0)
        {
            return false;
        }

        int filled = 0, nonEmpty = 0, numeric = 0;
        SortedDictionary<int, List<(int R, double V)>> cols = [];
        List<((int R, int C) Rc, double V)> flow = [];
        foreach (PlacedCell c in grid.Cells)
        {
            if (c.Text.Trim().Length == 0)
            {
                continue;
            }

            nonEmpty++;
            filled += (c.R1 - c.R0 + 1) * (c.C1 - c.C0 + 1);
            if (ParseNumber(c.Text) is double v)
            {
                numeric++;
                if (c.C0 == c.C1)
                {
                    if (!cols.TryGetValue(c.C0, out List<(int R, double V)>? list))
                    {
                        cols[c.C0] = list = [];
                    }

                    list.Add((c.R0, v));
                }

                flow.Add(((c.R0, c.C0), v));
            }
        }

        if (filled * 2 >= slots || numeric * 2 < nonEmpty)
        {
            return false;
        }

        Units.StableSort(flow, (a, b) =>
        {
            int r = a.Rc.R.CompareTo(b.Rc.R);
            return r != 0 ? r : a.Rc.C.CompareTo(b.Rc.C);
        });
        if (Stepped([.. flow.Select(f => f.V)]))
        {
            return true;
        }

        foreach (List<(int R, double V)> vs in cols.Values)
        {
            Units.StableSort(vs, (a, b) => a.R.CompareTo(b.R));
            if (Stepped([.. vs.Select(x => x.V)]))
            {
                return true;
            }
        }

        return false;
    }
}
