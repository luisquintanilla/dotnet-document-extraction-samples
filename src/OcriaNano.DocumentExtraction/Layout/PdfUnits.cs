// Ported from LiteParse crates/liteparse/src/table_model/units.rs (pdf_units), Apache-2.0.
using System.Text;
using UglyToad.PdfPig.Content;

namespace OcriaNano.DocumentExtraction.Layout;

internal static partial class Units
{
    /// <summary>Box in the reading frame of direction <paramref name="q"/> (LiteParse's <c>to_frame</c>).</summary>
    public static Box4 ToFrame(Box4 b, int q, double w, double h) => q switch
    {
        1 => new Box4(h - b.Y1, b.X0, h - b.Y0, b.X1),
        2 => new Box4(w - b.X1, h - b.Y1, w - b.X0, h - b.Y0),
        3 => new Box4(b.Y0, w - b.X1, b.Y1, w - b.X0),
        _ => b,
    };

    /// <summary>Inverse of <see cref="ToFrame"/> (LiteParse's <c>from_frame</c>).</summary>
    public static Box4 FromFrame(Box4 b, int q, double w, double h) => q switch
    {
        1 => new Box4(b.Y0, h - b.X1, b.Y1, h - b.X0),
        2 => new Box4(w - b.X1, h - b.Y1, w - b.X0, h - b.Y0),
        3 => new Box4(w - b.Y1, b.X0, w - b.Y0, b.X1),
        _ => b,
    };

    /// <summary>Stable sort (Rust's <c>sort_by</c>); <see cref="List{T}.Sort()"/> is not stable.</summary>
    public static void StableSort<T>(List<T> list, Comparison<T> cmp)
    {
        List<T> sorted = [.. list.OrderBy(x => x, Comparer<T>.Create(cmp))];
        list.Clear();
        list.AddRange(sorted);
    }

    public static double Median(List<double> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[(n / 2) - 1] + v[n / 2]) / 2.0;
    }

    public static int CharCount(string s)
    {
        int n = 0;
        foreach (Rune _ in s.EnumerateRunes())
        {
            n++;
        }

        return n;
    }
}

/// <summary>Builds units from a PdfPig page (port of <c>units::pdf_units</c>).</summary>
internal static partial class PdfUnits
{
    public const double CellGap = 1.8;
    private static readonly int[] s_leader = ['.', '·', '…', '•', 0xFFFD, '_'];
    private const int LeaderMin = 3;
    private const double NumGap = 0.5;
    private const double AdaptX = 2.5;
    private const double AdaptMin = 0.6;

    /// <summary>A space PdfPig didn't report (PDFium inserts one where the gap exceeds this share of the font size).</summary>
    private const double SyntheticSpaceEm = 0.2;

    private sealed class Ch
    {
        public required int[] C { get; init; }

        public Box4 Pb { get; init; }

        public double Size { get; init; }

        public bool? Bold { get; init; }

        public bool IsSpace => C.Length == 1 && IsWhite(C[0]);
    }

    private static bool IsWhite(int cp) => Rune.IsValid(cp) && Rune.IsWhiteSpace(new Rune(cp));

    private static string Decode(IEnumerable<int> cps)
    {
        StringBuilder sb = new();
        foreach (int cp in cps)
        {
            if (Rune.IsValid(cp))
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
        }

        return sb.ToString();
    }

    private static Box4 RotatePage(Box4 b, int rot, double w0, double h0) => rot switch
    {
        1 => new Box4(h0 - b.Y1, b.X0, h0 - b.Y0, b.X1),
        2 => new Box4(w0 - b.X1, h0 - b.Y1, w0 - b.X0, h0 - b.Y0),
        3 => new Box4(b.Y0, w0 - b.X1, b.Y1, w0 - b.X0),
        _ => b,
    };

    private static bool IsDec(char c) => c is >= '0' and <= '9';

    private static bool IsDash(char c) => c is '-' or '–' or '−';

    /// <summary>units.py _NUM: <c>^[-–−]?\$?\d[\d,.]*%?(?:[-–−]\d[\d,.]*%?)?$</c>.</summary>
    internal static bool IsNum(string t)
    {
        int i = 0;
        if (i < t.Length && IsDash(t[i]))
        {
            i++;
        }

        if (i < t.Length && t[i] == '$')
        {
            i++;
        }

        if (!Number(t, ref i))
        {
            return false;
        }

        if (i == t.Length)
        {
            return true;
        }

        if (IsDash(t[i]))
        {
            i++;
            if (Number(t, ref i) && i == t.Length)
            {
                return true;
            }
        }

        return false;

        static bool Number(string t, ref int i)
        {
            if (i >= t.Length || !IsDec(t[i]))
            {
                return false;
            }

            i++;
            while (i < t.Length && (IsDec(t[i]) || t[i] == ',' || t[i] == '.'))
            {
                i++;
            }

            if (i < t.Length && t[i] == '%')
            {
                i++;
            }

            return true;
        }
    }

    private static bool[] LeaderMask(List<(Ch C, Box4 B)> chars)
    {
        bool[] mask = new bool[chars.Count];
        int i = 0;
        while (i < chars.Count)
        {
            if (IsLead(chars[i].C))
            {
                int j = i, k = 0;
                while (j < chars.Count && (IsLead(chars[j].C) || chars[j].C.IsSpace))
                {
                    k += IsLead(chars[j].C) ? 1 : 0;
                    j++;
                }

                if (k >= LeaderMin)
                {
                    for (int m = i; m < j; m++)
                    {
                        mask[m] = true;
                    }
                }

                i = j;
            }
            else
            {
                i++;
            }
        }

        return mask;

        static bool IsLead(Ch c) => c.C.Length == 1 && Array.IndexOf(s_leader, c.C[0]) >= 0;
    }

    private static double? GutterGap(List<(Ch C, Box4 B)> chars, double h)
    {
        List<double> gaps = [];
        double? prev = null;
        bool spaced = false;
        foreach ((Ch c, Box4 b) in chars)
        {
            if (c.IsSpace)
            {
                spaced = true;
                continue;
            }

            if (prev is double p && spaced)
            {
                gaps.Add(b.X0 - p);
            }

            prev = b.X1;
            spaced = false;
        }

        if (gaps.Count < 3)
        {
            return null;
        }

        return Math.Max(AdaptX * Units.Median(gaps), AdaptMin * h);
    }

    private static List<Unit> Cut(List<(Ch C, Box4 B)> chars, double h)
    {
        bool[] lead = LeaderMask(chars);
        double? gut = GutterGap(chars, h);
        List<List<int>> groups = [];
        List<int> cur = [];
        double? prev = null;
        StringBuilder word = new();
        string lastWord = "";
        bool spaced = false;
        for (int k = 0; k < chars.Count; k++)
        {
            (Ch c, Box4 b) = chars[k];
            if (lead[k])
            {
                if (cur.Count > 0)
                {
                    groups.Add(cur);
                    cur = [];
                    prev = null;
                    word.Clear();
                    lastWord = "";
                    spaced = false;
                }

                continue;
            }

            if (c.IsSpace)
            {
                if (cur.Count > 0)
                {
                    cur.Add(k);
                    if (word.Length > 0)
                    {
                        lastWord = word.ToString();
                        word.Clear();
                    }

                    spaced = true;
                }

                continue;
            }

            bool isCut = prev is double p0 && b.X0 - p0 > CellGap * h;
            if (!isCut && spaced && gut is double g && prev is double p1 && b.X0 - p1 > g)
            {
                isCut = true;
            }

            if (!isCut && spaced && prev is double p2 && b.X0 - p2 > NumGap * h && IsNum(lastWord))
            {
                StringBuilder nxt = new(Decode(c.C));
                for (int x = k + 1; x < chars.Count && x < k + 24; x++)
                {
                    if (chars[x].C.IsSpace)
                    {
                        break;
                    }

                    nxt.Append(Decode(chars[x].C.C));
                }

                isCut = IsNum(nxt.ToString());
            }

            if (isCut)
            {
                groups.Add(cur);
                cur = [];
                lastWord = "";
            }

            cur.Add(k);
            word.Append(Decode(c.C));
            spaced = false;
            prev = b.X1;
        }

        if (cur.Count > 0)
        {
            groups.Add(cur);
        }

        List<Unit> output = [];
        foreach (List<int> g in groups)
        {
            List<int> ink = g.Where(k => !chars[k].C.IsSpace).ToList();
            if (ink.Count == 0)
            {
                continue;
            }

            string text = string.Join(' ', Decode(g.SelectMany(k => chars[k].C.C)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0)
            {
                continue;
            }

            Box4 bx = Box4.Empty;
            foreach (int k in ink)
            {
                Box4 b = chars[k].B;
                bx = new Box4(Math.Min(bx.X0, b.X0), Math.Min(bx.Y0, b.Y0), Math.Max(bx.X1, b.X1), Math.Max(bx.Y1, b.Y1));
            }

            List<double> hs = ink.Select(k => chars[k].B.Y1 - chars[k].B.Y0).ToList();
            List<double> sizes = ink.Select(k => chars[k].C.Size).ToList();
            List<bool> bolds = ink.Where(k => chars[k].C.Bold.HasValue).Select(k => chars[k].C.Bold!.Value).ToList();
            bool? bold = bolds.Count == 0 ? null : bolds.Count(x => x) > bolds.Count / 2.0;
            List<(double, double, string)> words = [];
            List<int> w = [];
            foreach (int? kk in g.Select(k => (int?)k).Append(null))
            {
                if (kk is int k2 && !chars[k2].C.IsSpace)
                {
                    w.Add(k2);
                }
                else if (w.Count > 0)
                {
                    double x0 = w.Min(k => chars[k].B.X0);
                    double x1 = w.Max(k => chars[k].B.X1);
                    words.Add((x0, x1, Decode(w.SelectMany(k => chars[k].C.C))));
                    w.Clear();
                }
            }

            output.Add(new Unit
            {
                Bx = bx,
                PBox = bx,
                Text = text,
                H = Units.Median(hs),
                Size = Units.Median(sizes),
                Bold = bold,
                Q = 0,
                Minor = false,
                Words = words,
            });
        }

        return output;
    }

    private static List<Unit> RunUnits(List<Ch> chars, int q, double w, double h, bool minor)
    {
        List<List<(Ch, Box4)>> runs = [];
        List<(Ch C, Box4 B)> cur = [];
        foreach (Ch c in chars)
        {
            Box4 bx = Units.ToFrame(c.Pb, q, w, h);
            if (cur.Count > 0 && !c.IsSpace)
            {
                int idx = cur.FindLastIndex(x => !x.C.IsSpace);
                if (idx >= 0)
                {
                    Box4 bb = cur[idx].B;
                    double hh = Math.Max(Math.Max(bb.Y1 - bb.Y0, bx.Y1 - bx.Y0), 1.0);
                    if (Math.Abs(((bx.Y0 + bx.Y1) / 2.0) - ((bb.Y0 + bb.Y1) / 2.0)) > 0.6 * hh || bx.X0 < bb.X0 - hh)
                    {
                        runs.Add([.. cur]);
                        cur.Clear();
                    }
                }
            }

            cur.Add((c, bx));
        }

        if (cur.Count > 0)
        {
            runs.Add([.. cur]);
        }

        List<Unit> units = [];
        foreach (List<(Ch C, Box4 B)> run in runs)
        {
            // the gap scale is a glyph height from letters and digits only (flat marks would pull the median to ~0)
            List<double> alnum = run
                .Where(x => x.C.C.Length == 1 && Rune.IsValid(x.C.C[0]) && IsAlnum(new Rune(x.C.C[0])))
                .Select(x => x.B.Y1 - x.B.Y0)
                .ToList();
            List<double> hs = alnum.Count == 0
                ? run.Where(x => !x.C.IsSpace).Select(x => x.B.Y1 - x.B.Y0).ToList()
                : alnum;
            if (hs.Count == 0)
            {
                continue;
            }

            units.AddRange(Cut(run, Units.Median(hs)));
        }

        foreach (Unit u in units)
        {
            u.PBox = Units.FromFrame(u.Bx, q, w, h);
            u.Q = (byte)q;
            u.Minor = minor;
        }

        return units;
    }

    private static bool IsAlnum(Rune r) => Rune.IsLetter(r) || Rune.IsNumber(r);

    /// <summary>Re-joins pieces the baseline rule split but that touch (a superscript, a symbol in another font).</summary>
    private static List<Unit> JoinTouching(List<Unit> units)
    {
        List<int> order = Enumerable.Range(0, units.Count).ToList();
        Units.StableSort(order, (a, b) => units[a].Bx.X0.CompareTo(units[b].Bx.X0));
        bool[] used = new bool[units.Count];
        List<Unit> output = [];
        foreach (int i in order)
        {
            if (used[i])
            {
                continue;
            }

            Unit u = units[i];
            Box4 bx = u.Bx;
            string text = u.Text;
            double h = u.H;
            List<(double X0, double X1, string Text)> words = [.. u.Words];
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (int j in order)
                {
                    if (used[j] || j == i)
                    {
                        continue;
                    }

                    Unit v = units[j];
                    if (v.Q != u.Q)
                    {
                        continue;
                    }

                    double hh = Math.Max(Math.Min(h, v.H), 1.0);
                    double gap = v.Bx.X0 - bx.X1;
                    if (-0.5 * hh <= gap && gap < 0.1 * hh && Math.Min(bx.Y1, v.Bx.Y1) - Math.Max(bx.Y0, v.Bx.Y0) > 0.0)
                    {
                        bx = bx.Union(v.Bx);
                        text += v.Text;
                        h = Math.Max(h, v.H);
                        int skip = 0;
                        if (words.Count > 0 && v.Words.Count > 0)
                        {
                            var a = words[^1];
                            var b = v.Words[0];
                            words[^1] = (Math.Min(a.X0, b.X0), Math.Max(a.X1, b.X1), a.Text + b.Text);
                            skip = 1;
                        }

                        words.AddRange(v.Words.Skip(skip));
                        used[j] = true;
                        grew = true;
                    }
                }
            }

            used[i] = true;
            output.Add(new Unit { Bx = bx, PBox = bx, Text = text, H = h, Size = u.Size, Bold = u.Bold, Q = u.Q, Minor = u.Minor, Words = words });
        }

        Units.StableSort(output, (a, b) =>
        {
            int c = ((long)Math.Round(a.Bx.Y0, MidpointRounding.ToEven)).CompareTo((long)Math.Round(b.Bx.Y0, MidpointRounding.ToEven));
            return c != 0 ? c : a.Bx.X0.CompareTo(b.Bx.X0);
        });
        return output;
    }

    /// <summary>
    /// A header row emitted as one run splits where its words line up with the table body: cut at every gap no nearby
    /// cell crosses, then fold back every piece that matches no shared column edge (units.py <c>_split_aligned</c>).
    /// </summary>
    internal static List<Unit> SplitAligned(List<Unit> units)
    {
        List<Unit> output = new(units.Count);
        for (int i = 0; i < units.Count; i++)
        {
            Unit u = units[i];
            List<Unit>? pieces = !u.Minor && u.Words.Count is >= 2 and <= 16 ? AlignedPieces(i, units) : null;
            if (pieces is not null)
            {
                output.AddRange(pieces);
            }
            else
            {
                output.Add(u.Clone());
            }
        }

        return output;
    }

    private static List<Unit>? AlignedPieces(int i, List<Unit> units)
    {
        Unit u = units[i];
        double x0 = u.Bx.X0, y0 = u.Bx.Y0, x1 = u.Bx.X1, y1 = u.Bx.Y1;
        double h = Math.Max(u.H, 1.0);
        double tol = Math.Max(0.25 * h, 1.5);
        static double Cy(Unit v) => (v.Bx.Y0 + v.Bx.Y1) / 2.0;
        List<(long Key, List<Unit> Cells)> rows = [];
        for (int j = 0; j < units.Count; j++)
        {
            Unit v = units[j];
            bool near = j != i
                && v.Q == u.Q
                && !v.Minor
                && v.Bx.X0 < x1 + (2.0 * h)
                && v.Bx.X1 > x0 - (2.0 * h)
                && ((y1 - (0.2 * h) <= v.Bx.Y0 && v.Bx.Y0 <= y1 + (6.0 * h))
                    || (y0 - (6.0 * h) <= v.Bx.Y1 && v.Bx.Y1 <= y0 + (0.2 * h)));
            int nw = v.Words.Count == 0 ? 1 : v.Words.Count;
            if (near
                && nw <= 4
                && v.Bx.X1 - v.Bx.X0 < 0.6 * (x1 - x0)
                && v.Bx.X0 >= x0 - tol
                && v.Bx.X1 <= x1 + (2.0 * h))
            {
                long key = (long)Math.Round(Cy(v) / h, MidpointRounding.ToEven);
                int at = rows.FindIndex(r => r.Key == key);
                if (at >= 0)
                {
                    rows[at].Cells.Add(v);
                }
                else
                {
                    rows.Add((key, [v]));
                }
            }
        }

        List<List<Unit>> tab = rows.Select(r => r.Cells).Where(r => r.Count >= 2).ToList();
        bool Datum(Unit v) => v.Text.Any(char.IsNumber) || Units.CharCount(v.Text) <= 3;
        bool loneOk = tab.Count == 1 && tab[0].Count >= 3 && 2 * tab[0].Count(Datum) > tab[0].Count;
        if (!(tab.Count >= 2 || loneOk))
        {
            return null;
        }

        List<Unit> cells = tab.SelectMany(r => r).ToList();
        List<double> Anchors(Func<Unit, double> f)
        {
            List<(double, double)> vals = cells.Select(v => (f(v), Cy(v))).ToList();
            if (tab.Count == 1)
            {
                return vals.Select(x => x.Item1).ToList();
            }

            return vals
                .Where(a => vals.Any(b => Math.Abs(a.Item1 - b.Item1) <= tol && Math.Abs(a.Item2 - b.Item2) > 0.5 * h))
                .Select(x => x.Item1)
                .ToList();
        }

        List<double> lefts = Anchors(v => v.Bx.X0);
        List<double> rights = Anchors(v => v.Bx.X1);
        List<double> mids = Anchors(v => (v.Bx.X0 + v.Bx.X1) / 2.0);
        var words = u.Words;
        bool Aligned(int a, int b)
        {
            double l = words[a].X0, r = words[b - 1].X1;
            return lefts.Any(x => Math.Abs(l - x) <= tol)
                || rights.Any(x => Math.Abs(r - x) <= tol)
                || mids.Any(x => Math.Abs(((l + r) / 2.0) - x) <= tol);
        }

        // no cell crosses mid, and every body row with cells on both sides leaves a 2+ glyph-height gap there, in two rows at least
        bool Gutter(double mid)
        {
            if (cells.Any(v => v.Bx.X0 < mid && mid < v.Bx.X1))
            {
                return false;
            }

            int spans = 0;
            foreach (List<Unit> r in tab)
            {
                double left = r.Where(v => v.Bx.X1 <= mid).Select(v => v.Bx.X1).DefaultIfEmpty(double.NegativeInfinity).Max();
                double right = r.Where(v => v.Bx.X0 >= mid).Select(v => v.Bx.X0).DefaultIfEmpty(double.PositiveInfinity).Min();
                if (double.IsFinite(left) && double.IsFinite(right))
                {
                    if (right - left < 2.0 * h)
                    {
                        return false;
                    }

                    spans++;
                }
            }

            return spans >= Math.Min(tab.Count, 2);
        }

        List<int> cuts = [0];
        for (int k = 1; k < words.Count; k++)
        {
            if (Gutter((words[k - 1].X1 + words[k].X0) / 2.0))
            {
                cuts.Add(k);
            }
        }

        cuts.Add(words.Count);
        while (cuts.Count >= 3)
        {
            int n0 = cuts.Count - 1;
            int bad = -1;
            for (int p = 0; p < n0; p++)
            {
                if (!Aligned(cuts[p], cuts[p + 1]))
                {
                    bad = p;
                    break;
                }
            }

            if (bad < 0)
            {
                break;
            }

            if (bad > 0 && Aligned(cuts[bad - 1], cuts[bad + 1]))
            {
                cuts.RemoveAt(bad);
            }
            else if (bad + 1 < n0 && Aligned(cuts[bad], cuts[bad + 2]))
            {
                cuts.RemoveAt(bad + 1);
            }
            else
            {
                return null;
            }
        }

        int n = cuts.Count - 1;
        if (n < 2 || Enumerable.Range(0, n).Any(p => cuts[p + 1] - cuts[p] > 5))
        {
            return null;
        }

        // a piece opening in lower case continues a sentence
        if (Enumerable.Range(1, n - 1).Any(p => words[cuts[p]].Text.EnumerateRunes().Any() && Rune.IsLower(words[cuts[p]].Text.EnumerateRunes().First())))
        {
            return null;
        }

        foreach (Unit v in cells)
        {
            int over = Enumerable.Range(0, n).Count(p => Math.Min(v.Bx.X1, words[cuts[p + 1] - 1].X1) > Math.Max(v.Bx.X0, words[cuts[p]].X0));
            if (over > 1)
            {
                return null;
            }
        }

        List<Unit> result = [];
        for (int p = 0; p < n; p++)
        {
            var ws = words.GetRange(cuts[p], cuts[p + 1] - cuts[p]);
            Box4 b = new(ws[0].X0, y0, ws[^1].X1, y1);
            result.Add(new Unit
            {
                Bx = b,
                PBox = b,
                Text = string.Join(' ', ws.Select(w => w.Text)),
                H = u.H,
                Size = u.Size,
                Bold = u.Bold,
                Q = u.Q,
                Minor = u.Minor,
                Words = ws,
            });
        }

        return result;
    }

    /// <summary>
    /// Single characters stacked in a column (vertical text emitted one glyph per run) are minor, unless most have
    /// row-mates: that is a table column of one-glyph cells.
    /// </summary>
    internal static void FlagLetterStacks(List<Unit> units)
    {
        List<int> singles = Enumerable.Range(0, units.Count).Where(i => Units.CharCount(units[i].Text) == 1 && !units[i].Minor).ToList();
        static double Cx(Unit u) => (u.PBox.X0 + u.PBox.X1) / 2.0;
        Units.StableSort(singles, (a, b) =>
        {
            int c = ((long)Math.Round(Cx(units[a]), MidpointRounding.ToEven)).CompareTo((long)Math.Round(Cx(units[b]), MidpointRounding.ToEven));
            return c != 0 ? c : units[a].PBox.Y0.CompareTo(units[b].PBox.Y0);
        });
        bool[] used = new bool[units.Count];
        for (int s = 0; s < singles.Count; s++)
        {
            int u = singles[s];
            if (used[u])
            {
                continue;
            }

            List<int> chain = [u];
            int last = u;
            for (int t = s + 1; t < singles.Count; t++)
            {
                int v = singles[t];
                double cl = Cx(units[last]), cv = Cx(units[v]);
                Box4 lb = units[last].PBox;
                double hh = Math.Max(Math.Max(lb.Y1 - lb.Y0, lb.X1 - lb.X0), 1.0);
                double dy = units[v].PBox.Y0 - lb.Y1;
                if (Math.Abs(cv - cl) < 0.6 * hh && 0.0 <= dy && dy < 1.5 * hh)
                {
                    chain.Add(v);
                    last = v;
                }
            }

            if (chain.Count >= 3 && !RowMates(units, chain))
            {
                foreach (int v in chain)
                {
                    units[v].Minor = true;
                    used[v] = true;
                }
            }
        }
    }

    private static bool RowMates(List<Unit> units, List<int> chain)
    {
        static double Cy(Unit u) => (u.PBox.Y0 + u.PBox.Y1) / 2.0;
        int mated = chain.Count(v =>
        {
            Box4 b = units[v].PBox;
            double tol = 0.3 * Math.Max(b.Y1 - b.Y0, 1.0);
            for (int w = 0; w < units.Count; w++)
            {
                if (!chain.Contains(w) && !units[w].Minor && Math.Abs(Cy(units[w]) - Cy(units[v])) < tol)
                {
                    return true;
                }
            }

            return false;
        });
        return mated * 5 >= chain.Count * 4;
    }

    private static bool? CharBold(Letter l)
    {
        var font = l.FontDetails;
        if (font is null)
        {
            return null;
        }

        string name = font.Name ?? "";
        return font.IsBold
            || font.Weight >= 600
            || name.Contains("Bold", StringComparison.Ordinal)
            || name.Contains("Black", StringComparison.Ordinal)
            || name.Contains("Heavy", StringComparison.Ordinal)
            || name.Contains("Semibold", StringComparison.Ordinal)
            || name.Contains(",B", StringComparison.Ordinal);
    }

    /// <summary>The page's units and its rendered size in points (width, height after <c>/Rotate</c>).</summary>
    public static (List<Unit> Units, double Width, double Height) FromPage(Page page)
    {
        var crop = page.CropBox.Bounds;
        double cl = crop.Left, cb = crop.Bottom, cr = crop.Right, ct = crop.Top;
        double w0 = cr - cl, h0 = ct - cb;
        int rot = (((int)page.Rotation.Value / 90) % 4 + 4) % 4;
        (double w, double h) = rot % 2 == 1 ? (h0, w0) : (w0, h0);

        // Some(char), or null for a line break (PDFium generates one; PdfPig reports none, so a jump to another line stands in)
        List<Ch?> chars = [];
        Ch? prev = null;
        foreach (Letter l in page.Letters)
        {
            string value = l.Value ?? "";
            if (value.Length == 0)
            {
                continue;
            }

            int[] cps = value.EnumerateRunes().Select(r => r.Value).ToArray();
            int first = cps[0];
            if (first == 0 || first is '\r' or '\n' or 2 or 0xFFFE)
            {
                continue;
            }

            var gb = l.BoundingBox;
            Box4 pb = RotatePage(new Box4(gb.Left - cl, ct - gb.Top, gb.Right - cl, ct - gb.Bottom), rot, w0, h0);
            bool space = cps.Length == 1 && IsWhite(first);
            if (pb.X1 <= pb.X0 && pb.Y1 <= pb.Y0 && !space)
            {
                continue;
            }

            double size = l.PointSize;
            Ch ch = new() { C = cps, Pb = pb, Size = size, Bold = CharBold(l) };
            if (!space && prev is not null)
            {
                double sz = Math.Max(prev.Size, size);
                double dx = (pb.X0 + pb.X1 - prev.Pb.X0 - prev.Pb.X1) / 2.0;
                double dy = (pb.Y0 + pb.Y1 - prev.Pb.Y0 - prev.Pb.Y1) / 2.0;
                bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
                double perp = horizontal ? Math.Abs(dy) : Math.Abs(dx);
                if (perp > 0.5 * sz)
                {
                    chars.Add(null);
                }
                else
                {
                    double gap = horizontal
                        ? (dx > 0 ? pb.X0 - prev.Pb.X1 : prev.Pb.X0 - pb.X1)
                        : (dy > 0 ? pb.Y0 - prev.Pb.Y1 : prev.Pb.Y0 - pb.Y1);
                    if (gap > SyntheticSpaceEm * sz)
                    {
                        chars.Add(new Ch { C = [' '], Pb = new Box4(prev.Pb.X1, prev.Pb.Y0, prev.Pb.X1, prev.Pb.Y1), Size = prev.Size });
                    }
                }
            }

            chars.Add(ch);
            prev = space ? null : ch;
        }

        // raw runs between line breaks
        List<List<Ch>> rawRuns = [];
        List<Ch> cur = [];
        foreach (Ch? c in chars)
        {
            if (c is null)
            {
                if (cur.Count > 0)
                {
                    rawRuns.Add(cur);
                    cur = [];
                }
            }
            else
            {
                cur.Add(c);
            }
        }

        if (cur.Count > 0)
        {
            rawRuns.Add(cur);
        }

        // reading direction per run by how consecutive glyphs advance; votes kept in first-seen order
        static List<(int Q, int N)> Direction(List<Ch> run)
        {
            List<(int Q, int N)> v = [];
            Ch? p = null;
            foreach (Ch c in run)
            {
                if (c.IsSpace)
                {
                    continue;
                }

                if (p is not null)
                {
                    double dx = (c.Pb.X0 + c.Pb.X1 - p.Pb.X0 - p.Pb.X1) / 2.0;
                    double dy = (c.Pb.Y0 + c.Pb.Y1 - p.Pb.Y0 - p.Pb.Y1) / 2.0;
                    double sz = Math.Max(Math.Max(c.Pb.X1 - c.Pb.X0, c.Pb.Y1 - c.Pb.Y0), 1.0);
                    double d = Math.Abs(dx) + Math.Abs(dy);
                    if (0.0 < d && d < 3.0 * sz)
                    {
                        int q = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0.0 ? 0 : 2) : (dy < 0.0 ? 1 : 3);
                        int at = v.FindIndex(e => e.Q == q);
                        if (at >= 0)
                        {
                            v[at] = (q, v[at].N + 1);
                        }
                        else
                        {
                            v.Add((q, 1));
                        }
                    }
                }

                p = c;
            }

            return v;
        }

        static int? Most(List<(int Q, int N)> v)
        {
            (int Q, int N)? best = null;
            foreach ((int k, int n) in v)
            {
                if (best is null || n > best.Value.N)
                {
                    best = (k, n);
                }
            }

            return best?.Q;
        }

        List<List<(int Q, int N)>> dirs = rawRuns.Select(Direction).ToList();
        List<(int Q, int N)> pageVotes = [];
        foreach (var d in dirs)
        {
            foreach ((int k, int n) in d)
            {
                int at = pageVotes.FindIndex(e => e.Q == k);
                if (at >= 0)
                {
                    pageVotes[at] = (k, pageVotes[at].N + n);
                }
                else
                {
                    pageVotes.Add((k, n));
                }
            }
        }

        int q0 = Most(pageVotes) ?? 0;
        List<Unit> units = [];
        for (int r = 0; r < rawRuns.Count; r++)
        {
            int total = dirs[r].Sum(x => x.N);
            int rq = dirs[r].Count > 0 && total >= 2 ? Most(dirs[r])!.Value : q0;
            units.AddRange(RunUnits(rawRuns[r], rq, w, h, rq != q0));
        }

        units = JoinTouching(units);
        units = SplitAligned(units);
        foreach (Unit u in units)
        {
            u.PBox = Units.FromFrame(u.Bx, u.Q, w, h);
        }

        FlagLetterStacks(units);
        return (units, w, h);
    }
}
