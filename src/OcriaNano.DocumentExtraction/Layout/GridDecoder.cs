// Ported from LiteParse crates/liteparse/src/table_model/decode.rs (decode_grid, span_link, squeeze, fold_*, to_html), Apache-2.0.
using System.Text;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>A grid cell while it is being decoded: top-left slot, span ends, text, box and the page-unit indices it holds.</summary>
internal sealed class PlacedCell
{
    public int R0 { get; set; }

    public int C0 { get; set; }

    public int R1 { get; set; }

    public int C1 { get; set; }

    public string Text { get; set; } = "";

    public Box4 Bx { get; set; }

    public List<int> Units { get; set; } = [];
}

/// <summary>A decoded grid before conversion to the public <see cref="Grid"/>.</summary>
internal sealed class DecodedGrid
{
    public List<PlacedCell> Cells { get; set; } = [];

    public List<int> HeadRows { get; set; } = [];

    public int NRows { get; set; }

    public int NCols { get; set; }

    public Grid ToGrid() => new(
        [.. Cells.Select(c => new GridCell(c.R0, c.C0, c.R1, c.C1, c.Text, c.Bx) { Units = [.. c.Units] })],
        [.. HeadRows],
        NRows,
        NCols);
}

internal static class GridDecoder
{
    public const float CellMargin = 1.5f;
    public const double StackGap = 0.8;
    public const double StackGapHdr = 0.8;
    public const double Gate = 3.0;

    /// <summary>Units positive with two groups that are negative with each other (a spanning header).</summary>
    private static List<int> Bridges(double[] p, int n)
    {
        const double neg = -1.0, frac = 0.3;
        List<int> output = [];
        for (int i = 0; i < n; i++)
        {
            List<int> nb = [.. Enumerable.Range(0, n).Where(j => j != i && p[(i * n) + j] > 0.0)];
            if (nb.Count < 2)
            {
                continue;
            }

            int tot = 0, negc = 0;
            for (int a = 0; a < nb.Count; a++)
            {
                for (int b = a + 1; b < nb.Count; b++)
                {
                    tot++;
                    if (p[(nb[a] * n) + nb[b]] < neg)
                    {
                        negc++;
                    }
                }
            }

            if (tot > 0 && (negc / (double)tot) > frac)
            {
                output.Add(i);
            }
        }

        return output;
    }

    /// <summary>Rows or columns: linkage over the non-bridging units; each bridging unit then joins every group it is positive with on average, or its best group.</summary>
    internal static List<List<int>> SpanLink(double[] p, int n)
    {
        List<int> br = Bridges(p, n);
        List<int> core = [.. Enumerable.Range(0, n).Where(i => !br.Contains(i))];
        if (core.Count == 0)
        {
            return AvgLinkage.AvgLink(p, n, 0.0);
        }

        int m = core.Count;
        double[] sub = new double[m * m];
        for (int a = 0; a < m; a++)
        {
            for (int b = 0; b < m; b++)
            {
                sub[(a * m) + b] = p[(core[a] * n) + core[b]];
            }
        }

        List<List<int>> groups = [.. AvgLinkage.AvgLink(sub, m, 0.0).Select(g => g.Select(i => core[i]).ToList())];
        foreach (int i in br)
        {
            double[] means = [.. groups.Select(g => g.Sum(j => p[(i * n) + j]) / g.Count)];
            List<int> hit = [.. Enumerable.Range(0, groups.Count).Where(k => means[k] > 0.0)];
            if (hit.Count == 0)
            {
                int best = 0;
                for (int k = 1; k < means.Length; k++)
                {
                    if (means[k] > means[best])
                    {
                        best = k;
                    }
                }

                hit = [best];
            }

            foreach (int k in hit)
            {
                groups[k].Add(i);
            }
        }

        foreach (List<int> g in groups)
        {
            g.Sort();
        }

        return groups;
    }

    /// <summary>One table's grid from its units (global indices <paramref name="idx"/>) and the row / col / cell heads.</summary>
    public static DecodedGrid Decode(IReadOnlyList<DUnit> units, IReadOnlyList<int> idx, Logits l, DecodeOptions opt)
    {
        int m = idx.Count;
        if (m == 0)
        {
            return new DecodedGrid();
        }

        float[][] bxs = [.. idx.Select(i =>
        {
            Box4 b = units[i].Bx;
            return new[] { (float)b.X0, (float)b.Y0, (float)b.X1, (float)b.Y1 };
        })];
        float[] hh = [.. bxs.Select(b => MathF.Max(b[3] - b[1], 1e-3f))];
        float Pr(int k, int a, int b) => l.Pair(k, idx[a], idx[b]);
        double[] cellP = new double[m * m];
        bool[] forced = new bool[m * m];
        for (int a = 0; a < m; a++)
        {
            for (int b = 0; b < m; b++)
            {
                float vgap = MathF.Max(bxs[b][1] - bxs[a][3], bxs[a][1] - bxs[b][3]);
                float xov = MathF.Min(bxs[a][2], bxs[b][2]) - MathF.Max(bxs[a][0], bxs[b][0]);
                bool bothHdr = l.Node(idx[a], 1) > 0f && l.Node(idx[b], 1) > 0f;
                double lim = (bothHdr ? StackGapHdr : StackGap) * MathF.Min(hh[a], hh[b]);
                bool stacked = vgap < lim && vgap > -0.5f * hh[a] && xov > 0f;
                float f = stacked ? MathF.Min(Pr(1, a, b), Pr(2, a, b)) - CellMargin : float.NegativeInfinity;
                forced[(a * m) + b] = f > 0f;
                cellP[(a * m) + b] = MathF.Max(Pr(3, a, b), f);
            }
        }

        List<List<int>> cells = AvgLinkage.AvgLink(cellP, m, 0.0);
        if (opt.ForcedLink && cells.Count > 1)
        {
            int[] of = new int[m];
            for (int k = 0; k < cells.Count; k++)
            {
                foreach (int i in cells[k])
                {
                    of[i] = k;
                }
            }

            int[] parent = [.. Enumerable.Range(0, cells.Count)];
            static int Root(int[] pa, int k)
            {
                while (pa[k] != k)
                {
                    pa[k] = pa[pa[k]];
                    k = pa[k];
                }

                return k;
            }

            for (int a = 0; a < m; a++)
            {
                for (int b = 0; b < m; b++)
                {
                    if (forced[(a * m) + b])
                    {
                        int ra = Root(parent, of[a]), rb = Root(parent, of[b]);
                        if (ra != rb)
                        {
                            parent[ra] = rb;
                        }
                    }
                }
            }

            List<int> keys = [];
            List<List<int>> merged = [];
            for (int k = 0; k < cells.Count; k++)
            {
                int r = Root(parent, k);
                int pos = keys.IndexOf(r);
                if (pos >= 0)
                {
                    merged[pos].AddRange(cells[k]);
                }
                else
                {
                    keys.Add(r);
                    merged.Add([.. cells[k]]);
                }
            }

            foreach (List<int> g in merged)
            {
                g.Sort();
            }

            cells = merged;
        }

        int nc = cells.Count;
        Box4[] cb = [.. cells.Select(g => g.Aggregate(Box4.Empty, (acc, i) => acc.Union(units[idx[i]].Bx)))];

        double[] CellLogits(int k, int lo, int hi)
        {
            double[] output = new double[nc * nc];
            for (int a = 0; a < nc; a++)
            {
                for (int b = a + 1; b < nc; b++)
                {
                    double s = 0.0;
                    foreach (int i in cells[a])
                    {
                        foreach (int j in cells[b])
                        {
                            s += Pr(k, i, j);
                        }
                    }

                    double v = s / (cells[a].Count * cells[b].Count);
                    if (Math.Min(cb[a][hi], cb[b][hi]) - Math.Max(cb[a][lo], cb[b][lo]) <= 0.0)
                    {
                        v -= Gate;
                    }

                    output[(a * nc) + b] = v;
                    output[(b * nc) + a] = v;
                }
            }

            return output;
        }

        List<List<int>> rows, cols;
        if (nc > 1)
        {
            rows = SpanLink(CellLogits(1, 1, 3), nc);
            cols = SpanLink(CellLogits(2, 0, 2), nc);
        }
        else
        {
            rows = [[0]];
            cols = [[0]];
        }

        double MeanOf(List<int> g, Func<int, double> f) => g.Sum(f) / g.Count;
        double YCen(int k) => (cb[k].Y0 + cb[k].Y1) / 2.0;
        double XCen(int k) => (cb[k].X0 + cb[k].X1) / 2.0;
        Units.StableSort(rows, (a, b) => MeanOf(a, YCen).CompareTo(MeanOf(b, YCen)));
        Units.StableSort(cols, (a, b) => MeanOf(a, XCen).CompareTo(MeanOf(b, XCen)));
        List<int>[] rIn = [.. Enumerable.Range(0, nc).Select(_ => new List<int>())];
        List<int>[] cIn = [.. Enumerable.Range(0, nc).Select(_ => new List<int>())];
        for (int k = 0; k < rows.Count; k++)
        {
            foreach (int c in rows[k])
            {
                rIn[c].Add(k);
            }
        }

        for (int k = 0; k < cols.Count; k++)
        {
            foreach (int c in cols[k])
            {
                cIn[c].Add(k);
            }
        }

        int nRows0 = rows.Count, nCols0 = cols.Count;
        List<PlacedCell> placed = [];
        int[] owner = new int[nRows0 * nCols0];
        Array.Fill(owner, -1);
        List<int> order = [.. Enumerable.Range(0, nc)];
        Units.StableSort(order, (a, b) =>
        {
            int c = rIn[a].Min().CompareTo(rIn[b].Min());
            return c != 0 ? c : cIn[a].Min().CompareTo(cIn[b].Min());
        });
        foreach (int c in order)
        {
            int r0 = rIn[c].Min(), r1 = rIn[c].Max();
            int c0 = cIn[c].Min(), c1 = cIn[c].Max();
            List<Box4> boxes = [.. cells[c].Select(i => units[idx[i]].Bx)];
            string text = string.Join(" ", ReadingOrder.Order(boxes).Select(k => units[idx[cells[c][k]]].Text));
            List<(int R, int Q)> span = [];
            for (int r = r0; r <= r1; r++)
            {
                for (int q = c0; q <= c1; q++)
                {
                    span.Add((r, q));
                }
            }

            if (span.Any(s => owner[(s.R * nCols0) + s.Q] != -1))
            {
                (int R, int Q)? free = null;
                foreach ((int R, int Q) s in span)
                {
                    if (owner[(s.R * nCols0) + s.Q] == -1)
                    {
                        free = s;
                        break;
                    }
                }

                if (free is { } f)
                {
                    r0 = r1 = f.R;
                    c0 = c1 = f.Q;
                }
                else
                {
                    PlacedCell o = placed[owner[(r0 * nCols0) + c0]];
                    o.Text = $"{o.Text} {text}";
                    o.Units.AddRange(cells[c].Select(i => idx[i]));
                    continue;
                }
            }

            int pi = placed.Count;
            placed.Add(new PlacedCell { R0 = r0, C0 = c0, R1 = r1, C1 = c1, Text = text, Bx = cb[c], Units = [.. cells[c].Select(i => idx[i])] });
            for (int r = r0; r <= r1; r++)
            {
                for (int q = c0; q <= c1; q++)
                {
                    owner[(r * nCols0) + q] = pi;
                }
            }
        }

        List<int> headRows = [];
        for (int k = 0; k < rows.Count; k++)
        {
            List<int> us = [.. rows[k].SelectMany(c => cells[c])];
            double pos = us.Count(i => l.Node(idx[i], 1) > 0f);
            if (pos / us.Count > 0.5)
            {
                headRows.Add(k);
            }
        }

        int nCols = nCols0;
        if (opt.Squeeze)
        {
            double hhm = TableDecoder.MedianH(units, idx);
            SqueezeCols(placed, headRows, ref nCols, hhm);
        }

        int nRows = nRows0;
        if (opt.FoldWraps)
        {
            FoldWrappedRows(placed, headRows, ref nRows);
            FoldHeaderWraps(placed, headRows, ref nRows);
        }

        return new DecodedGrid { Cells = placed, HeadRows = headRows, NRows = nRows, NCols = nCols };
    }

    /// <summary>Fold near-empty columns into their nearest neighbour (tn_core._squeeze, column rule).</summary>
    internal static void SqueezeCols(List<PlacedCell> cells, List<int> head, ref int nCols, double hh)
    {
        bool changed = true;
        while (changed && nCols >= 3)
        {
            changed = false;
            for (int q = 0; q < nCols; q++)
            {
                List<int> col = [.. Enumerable.Range(0, cells.Count).Where(k => cells[k].C0 == q && cells[k].C1 == q && cells[k].Text.Length != 0)];
                if (col.Count(k => !head.Contains(cells[k].R0)) > 1)
                {
                    continue;
                }

                List<Box4> boxes = [.. col.Select(k => cells[k].Bx)];
                (double Gap, int T)? best = null;
                foreach (int t in new[] { q - 1, q + 1 })
                {
                    if (t < 0 || t >= nCols)
                    {
                        continue;
                    }

                    List<Box4> nb = [.. cells.Where(x => x.C0 <= t && t <= x.C1 && x.C0 == x.C1 && x.Text.Length != 0).Select(x => x.Bx)];
                    if (boxes.Count == 0 || nb.Count == 0)
                    {
                        continue;
                    }

                    double gap = double.PositiveInfinity;
                    foreach (Box4 b in boxes)
                    {
                        foreach (Box4 nn in nb)
                        {
                            gap = Math.Min(gap, Math.Max(Math.Max(b.X0 - nn.X1, nn.X0 - b.X1), 0.0));
                        }
                    }

                    if (gap < 1.5 * hh && (best is null || gap < best.Value.Gap))
                    {
                        best = (gap, t);
                    }
                }

                if (best is { } bt)
                {
                    FoldCol(cells, q, bt.T);
                    nCols--;
                    changed = true;
                    break;
                }
            }
        }
    }

    internal static void FoldCol(List<PlacedCell> cells, int k, int t)
    {
        List<int> movers = [.. Enumerable.Range(0, cells.Count).Where(i => cells[i].C0 == k && cells[i].C1 == k)];
        List<int> remove = [];
        foreach (int xi in movers)
        {
            int r = cells[xi].R0;
            int tgt = -1;
            for (int j = 0; j < cells.Count; j++)
            {
                if (cells[j].R0 <= r && r <= cells[j].R1 && cells[j].C0 <= t && t <= cells[j].C1)
                {
                    tgt = j;
                    break;
                }
            }

            if (tgt >= 0 && tgt != xi)
            {
                if (cells[xi].Text.Length != 0)
                {
                    string joined = t < k ? $"{cells[tgt].Text} {cells[xi].Text}" : $"{cells[xi].Text} {cells[tgt].Text}";
                    cells[tgt].Text = RustTrim(joined);
                }

                cells[tgt].Units.AddRange(cells[xi].Units);
                remove.Add(xi);
            }
            else
            {
                cells[xi].C0 = t;
                cells[xi].C1 = t;
            }
        }

        remove.Sort();
        for (int i = remove.Count - 1; i >= 0; i--)
        {
            cells.RemoveAt(remove[i]);
        }

        foreach (PlacedCell x in cells)
        {
            if (x.C0 <= k && k <= x.C1 && x.C0 < x.C1)
            {
                x.C1--;
            }
            else if (x.C0 > k)
            {
                x.C0--;
                x.C1--;
            }
        }
    }

    private static string RustTrim(string s) => s.Trim();

    /// <summary>A lone number (thousands separators, sign, decimal, percent): values don't wrap.</summary>
    internal static bool IsNumber(string text)
    {
        string t = text.Trim().TrimEnd('%').Replace(",", "").Replace(" ", "");
        return t.Length != 0 && t.Any(c => c is >= '0' and <= '9') && t.All(c => c is >= '0' and <= '9' || "+-.()$€£".Contains(c));
    }

    /// <summary>Text opening a new list item ("1.2 ...", "(a) ...", "• ..."): a sub-row, never a wrapped line.</summary>
    internal static bool StartsItem(string text)
    {
        string t = text.TrimStart();
        string? first = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null)
        {
            return false;
        }

        if (Units.CharCount(first) == 1 && "•·▪◦‣-–—*●○■□".Contains(first, StringComparison.Ordinal))
        {
            return true;
        }

        string core = first.TrimStart('(').TrimEnd(')', '.', ':');
        bool enumerator = core.Length != 0
            && (core.Split('.').All(p => p.Length != 0 && p.All(c => c is >= '0' and <= '9'))
                || (core.Length == 1 && char.IsAsciiLetter(core[0]) && first != core));
        return enumerator && first != t;
    }

    private static List<int> Filled(List<PlacedCell> cells, int r) =>
        [.. Enumerable.Range(0, cells.Count).Where(k => cells[k].R0 == r && cells[k].R1 == r && cells[k].Text.Trim().Length != 0)];

    private static void Absorb(PlacedCell into, PlacedCell from)
    {
        string t = from.Text.Trim();
        Box4 b = from.Bx;
        into.Text = $"{into.Text.Trim()} {t}";
        into.Bx = new Box4(Math.Min(into.Bx.X0, b.X0), Math.Min(into.Bx.Y0, b.Y0), Math.Max(into.Bx.X1, b.X1), Math.Max(into.Bx.Y1, b.Y1));
        into.Units.AddRange(from.Units);
    }

    /// <summary>Rows that are a wrapped cell's continuation lines fold into the row above (see the Rust doc comment).</summary>
    internal static void FoldWrappedRows(List<PlacedCell> cells, List<int> head, ref int nRows)
    {
        static double Top(List<PlacedCell> cs, List<int> ks) => ks.Select(k => cs[k].Bx.Y0).Aggregate(double.PositiveInfinity, Math.Min);
        List<double> pitches = [];
        for (int r = 1; r < nRows; r++)
        {
            List<int> a = Filled(cells, r - 1), b = Filled(cells, r);
            if (a.Count >= 2 && b.Count >= 2)
            {
                pitches.Add(Top(cells, b) - Top(cells, a));
            }
        }

        if (pitches.Count < 2)
        {
            return;
        }

        double pitch = Units.Median(pitches);
        int row = 1;
        while (row < nRows)
        {
            List<int> above = Filled(cells, row - 1), here = Filled(cells, row);
            bool sameKind = head.Contains(row) == head.Contains(row - 1);
            List<int> targets = [];
            bool spanStarts = cells.Any(c => c.R0 == row && c.R1 > row);
            bool ok = sameKind && !spanStarts && here.Count != 0 && here.Count < above.Count;
            if (ok)
            {
                foreach (int k in here)
                {
                    PlacedCell c = cells[k];
                    int t = -1;
                    for (int j = 0; j < cells.Count; j++)
                    {
                        if (cells[j].R0 < row && row - 1 <= cells[j].R1 && cells[j].C0 <= c.C0 && c.C1 <= cells[j].C1)
                        {
                            t = j;
                            break;
                        }
                    }

                    if (t >= 0 && cells[t].Text.Trim().Length != 0 && !IsNumber(c.Text) && !StartsItem(c.Text))
                    {
                        bool good = c.Bx.Y0 - cells[t].Bx.Y0 < 0.8 * pitch && c.Bx.Y0 - cells[t].Bx.Y1 < 0.5 * pitch;
                        targets.Add(t);
                        if (!good)
                        {
                            ok = false;
                            break;
                        }
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
            {
                row++;
                continue;
            }

            for (int i = 0; i < here.Count; i++)
            {
                Absorb(cells[targets[i]], cells[here[i]]);
            }

            RemoveRow(cells, head, ref nRows, row);
        }
    }

    /// <summary>Drop row <paramref name="r"/> after its text moved up: its (empty) cells go, spans through it shrink, rows below move up.</summary>
    internal static void RemoveRow(List<PlacedCell> cells, List<int> head, ref int nRows, int r)
    {
        cells.RemoveAll(c => c.R0 == r && c.R1 == r);
        foreach (PlacedCell c in cells)
        {
            if (c.R0 > r)
            {
                c.R0--;
                c.R1--;
            }
            else if (c.R1 >= r)
            {
                c.R1--;
            }
        }

        head.RemoveAll(h => h == r);
        for (int i = 0; i < head.Count; i++)
        {
            if (head[i] > r)
            {
                head[i]--;
            }
        }

        nRows--;
    }

    /// <summary>Whether <paramref name="below"/> reads as the next line of the same phrase as <paramref name="above"/>.</summary>
    internal static bool Continues(string above, string below)
    {
        string a = above.Trim(), b = below.Trim();
        bool opens = b.Length != 0 && (char.IsLower(b[0]) || "([{".Contains(b[0]));
        bool openBrackets = a.Count(c => c == '(') > a.Count(c => c == ')');
        int ws = a.LastIndexOfAny([' ', '\t', '\n', '\r', '\u00A0']);
        string lastWord = (ws >= 0 ? a[(ws + 1)..] : a).ToLowerInvariant();
        bool breaks = (a.Length != 0 && "-/,(&".Contains(a[^1]))
            || openBrackets
            || Array.IndexOf(["and", "of", "the", "for", "to", "in", "on", "or", "per", "by", "with", "from"], lastWord) >= 0;
        return opens || breaks;
    }

    /// <summary>Header cells wrapped over several lines come out as one header row per line; fold them (see the Rust doc comment).</summary>
    internal static void FoldHeaderWraps(List<PlacedCell> cells, List<int> head, ref int nRows)
    {
        static int Zone(List<PlacedCell> cs, List<int> hd, int nr)
        {
            int marked = 0;
            while (marked < nr && hd.Contains(marked))
            {
                marked++;
            }

            int z = 0;
            while (z < Math.Min(nr, 4))
            {
                List<int> f = Filled(cs, z);
                if (f.Count == 0 || f.Any(k => IsNumber(cs[k].Text)))
                {
                    break;
                }

                z++;
            }

            return Math.Min(Math.Max(z, marked), 4);
        }

        int r = 1;
        while (r < Zone(cells, head, nRows))
        {
            List<int> here = Filled(cells, r);
            bool spanStarts = cells.Any(c => c.R0 == r && c.R1 > r);
            List<(int K, int J)> pairs = [];
            int nCols = cells.Count == 0 ? 0 : cells.Max(c => c.C1 + 1);
            bool ok = (here.Count >= 2 || nCols <= 2) && !spanStarts;
            if (ok)
            {
                foreach (int k in here)
                {
                    PlacedCell c = cells[k];
                    int j = -1;
                    for (int x = 0; x < cells.Count; x++)
                    {
                        if (cells[x].R0 < r && r - 1 <= cells[x].R1 && cells[x].C0 <= c.C0 && c.C1 <= cells[x].C1 && cells[x].Text.Trim().Length != 0)
                        {
                            j = x;
                            break;
                        }
                    }

                    if (j >= 0 && !IsNumber(c.Text) && !StartsItem(c.Text))
                    {
                        double lineH = Math.Max(c.Bx.Y1 - c.Bx.Y0, 1e-6);
                        pairs.Add((k, j));
                        if (!(c.Bx.Y0 - cells[j].Bx.Y1 < 0.6 * lineH && Continues(cells[j].Text, c.Text)))
                        {
                            ok = false;
                            break;
                        }
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
            {
                r++;
                continue;
            }

            foreach ((int k, int j) in pairs)
            {
                Absorb(cells[j], cells[k]);
            }

            RemoveRow(cells, head, ref nRows, r);
        }
    }

    /// <summary>Python <c>html.escape(text, quote=True)</c>.</summary>
    private static string Escape(string t)
    {
        StringBuilder s = new(t.Length);
        foreach (char ch in t)
        {
            s.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#x27;",
                _ => ch.ToString(),
            });
        }

        return s.ToString();
    }

    /// <summary>The grid as HTML, exactly as tn_core.decode_grid writes it.</summary>
    public static string ToHtml(this Grid grid)
    {
        Dictionary<(int, int), int> at = [];
        HashSet<(int, int)> taken = [];
        for (int k = 0; k < grid.Cells.Count; k++)
        {
            GridCell c = grid.Cells[k];
            at[(c.R0, c.C0)] = k;
            for (int r = c.R0; r <= c.R1; r++)
            {
                for (int q = c.C0; q <= c.C1; q++)
                {
                    taken.Add((r, q));
                }
            }
        }

        List<string> output = ["<table>"];
        for (int r = 0; r < grid.NRows; r++)
        {
            string tag = grid.HeadRows.Contains(r) ? "th" : "td";
            StringBuilder tds = new();
            int q = 0;
            while (q < grid.NCols)
            {
                if (at.TryGetValue((r, q), out int k))
                {
                    GridCell c = grid.Cells[k];
                    string attr = "";
                    if (c.R1 > r)
                    {
                        attr += $" rowspan=\"{c.R1 - r + 1}\"";
                    }

                    if (c.C1 > q)
                    {
                        attr += $" colspan=\"{c.C1 - q + 1}\"";
                    }

                    tds.Append($"<{tag}{attr}>{Escape(c.Text)}</{tag}>");
                    q = c.C1 + 1;
                }
                else if (taken.Contains((r, q)))
                {
                    q++;
                }
                else
                {
                    tds.Append($"<{tag}></{tag}>");
                    q++;
                }
            }

            output.Add($"<tr>{tds}</tr>");
        }

        output.Add("</table>");
        return string.Join("\n", output);
    }
}
