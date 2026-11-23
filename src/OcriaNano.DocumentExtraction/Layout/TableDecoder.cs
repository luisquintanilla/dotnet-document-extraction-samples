// Ported from LiteParse crates/liteparse/src/table_model/decode.rs (decode_tables, head_band, is_caption), Apache-2.0.
namespace OcriaNano.DocumentExtraction.Layout;

internal static class TableDecoder
{
    /// <summary>Median unit height, or 1 when it is 0 (<c>float(np.median(...)) or 1.0</c>).</summary>
    internal static double MedianH(IReadOnlyList<DUnit> units, IEnumerable<int> idx)
    {
        List<double> hs = [.. idx.Select(i => units[i].H)];
        if (hs.Count == 0)
        {
            return 1.0;
        }

        double m = Units.Median(hs);
        return m == 0.0 || double.IsNaN(m) ? 1.0 : m;
    }

    internal static Box4 BBoxOf(IReadOnlyList<DUnit> units, IEnumerable<int> g)
    {
        Box4 b = Box4.Empty;
        foreach (int i in g)
        {
            b = b.Union(units[i].Bx);
        }

        return b;
    }

    /// <summary><c>^\s*(table|tab\.|figure|fig\.|exhibit|chart|source|note)s?\b</c>, case-insensitive.</summary>
    internal static bool IsCaption(string text)
    {
        string t = text.TrimStart().ToLowerInvariant();
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        bool Boundary(int pos)
        {
            bool prev = pos > 0 && Word(t[pos - 1]);
            bool next = pos < t.Length && Word(t[pos]);
            return prev != next;
        }

        foreach (string w in new[] { "table", "tab.", "figure", "fig.", "exhibit", "chart", "source", "note" })
        {
            if (!t.StartsWith(w, StringComparison.Ordinal))
            {
                continue;
            }

            int end = w.Length;
            if (end < t.Length && t[end] == 's' && Boundary(end + 1))
            {
                return true;
            }

            if (Boundary(end))
            {
                return true;
            }
        }

        return false;
    }

    private static void HeadBand(IReadOnlyList<DUnit> units, Logits l, List<List<int>> groups, bool[] taken)
    {
        double hm = MedianH(units, Enumerable.Range(0, units.Count));
        foreach (List<int> g in groups)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Box4 b = BBoxOf(units, g);
                double x0 = b.X0, y0 = b.Y0, x1 = b.X1;
                List<int> above = [];
                for (int i = 0; i < units.Count; i++)
                {
                    Box4 u = units[i].Bx;
                    if (!taken[i] && l.Node(i, 0) > -8.0f && u.Y1 <= y0 + (0.2 * hm) && y0 - u.Y1 < 4.0 * 1.4 * hm)
                    {
                        above.Add(i);
                    }
                }

                if (above.Count == 0)
                {
                    break;
                }

                int low = above[0];
                foreach (int i in above.Skip(1))
                {
                    if (units[i].Bx.Y1 > units[low].Bx.Y1)
                    {
                        low = i;
                    }
                }

                Box4 lb = units[low].Bx;
                List<int> line = [.. above.Where(i => Math.Min(units[i].Bx.Y1, lb.Y1) - Math.Max(units[i].Bx.Y0, lb.Y0) > 0.3 * hm)];
                double w = x1 - x0;
                bool ok = line.All(i => units[i].Bx.X0 >= x0 - (0.05 * w) && units[i].Bx.X1 <= x1 + (0.05 * w));
                bool near = y0 - line.Select(i => units[i].Bx.Y1).Aggregate(double.NegativeInfinity, Math.Max) < 1.5 * 1.4 * hm;
                bool strong = line.All(i => l.Node(i, 0) > -1.5f);
                bool over = line.All(i => g.Any(j => Math.Min(units[i].Bx.X1, units[j].Bx.X1) - Math.Max(units[i].Bx.X0, units[j].Bx.X0) > 0.0));
                ok = ok && (strong || (near && over && line.All(i => l.Node(i, 0) > -4.0f)));
                ok = ok && !line.Any(i => IsCaption(units[i].Text));
                double hdr = line.Count(i => l.Node(i, 1) > 0.0f) / (double)Math.Max(line.Count, 1);
                ok = ok && line.Count >= 2 && hdr > 0.5;
                ok = ok && !line.Any(i =>
                {
                    string t = units[i].Text;
                    string tr = t.TrimEnd();
                    return t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 6 || tr.EndsWith('.') || tr.EndsWith(':');
                });
                if (!ok)
                {
                    break;
                }

                foreach (int i in line)
                {
                    g.Add(i);
                    taken[i] = true;
                }
            }

            g.Sort();
        }
    }

    /// <summary>Tables as lists of unit indices (tn_core.decode_tables, absorb=True).</summary>
    public static List<List<int>> Decode(IReadOnlyList<DUnit> units, Logits l, DecodeOptions opt)
    {
        int n = units.Count;
        List<int> on = [.. Enumerable.Range(0, n).Where(i => l.Node(i, 0) > opt.TableThresh)];
        if (on.Count < 2)
        {
            return [];
        }

        int m = on.Count;
        double[] p = new double[m * m];
        for (int a = 0; a < m; a++)
        {
            for (int b = 0; b < m; b++)
            {
                p[(a * m) + b] = l.Pair(0, on[a], on[b]);
            }
        }

        List<List<int>> groups = [.. AvgLinkage.AvgLink(p, m, 0.0).Where(g => g.Count >= 2).Select(g => g.Select(i => on[i]).ToList())];
        if (groups.Count > 1)
        {
            double hm = MedianH(units, Enumerable.Range(0, n));
            while (true)
            {
                Box4[] ext = [.. groups.Select(g => BBoxOf(units, g))];
                (int, int)? merged = null;
                for (int a = 0; a < groups.Count && merged is null; a++)
                {
                    for (int b = 0; b < groups.Count; b++)
                    {
                        if (a == b)
                        {
                            continue;
                        }

                        Box4 aa = ext[a], bb = ext[b];
                        double gap = bb.Y0 - aa.Y1;
                        double ov = Math.Min(aa.X1, bb.X1) - Math.Max(aa.X0, bb.X0);
                        if (gap >= 0.0 && gap < 3.0 * 1.4 * hm && ov > 0.7 * Math.Min(aa.X1 - aa.X0, bb.X1 - bb.X0))
                        {
                            merged = (a, b);
                            break;
                        }
                    }
                }

                if (merged is not (int ma, int mb))
                {
                    break;
                }

                groups[ma].AddRange(groups[mb]);
                groups[ma].Sort();
                groups.RemoveAt(mb);
            }
        }

        if (groups.Count > 1)
        {
            double hm = MedianH(units, Enumerable.Range(0, n));
            while (true)
            {
                Units.StableSort(groups, (a, b) => b.Count.CompareTo(a.Count));
                Box4[] ext = [.. groups.Select(g =>
                {
                    Box4 b = BBoxOf(units, g);
                    return new Box4(b.X0 - (1.4 * hm), b.Y0 - (1.4 * hm), b.X1 + (1.4 * hm), b.Y1 + (1.4 * hm));
                })];
                (int, int)? merged = null;
                for (int s = groups.Count - 1; s >= 1 && merged is null; s--)
                {
                    Box4 sb = BBoxOf(units, groups[s]);
                    double area = Math.Max((sb.X1 - sb.X0) * (sb.Y1 - sb.Y0), 1e-6);
                    for (int b = 0; b < s; b++)
                    {
                        if (groups[s].Count > 0.5 * groups[b].Count)
                        {
                            continue;
                        }

                        Box4 e = ext[b];
                        double ov = Math.Max(Math.Min(sb.X1, e.X1) - Math.Max(sb.X0, e.X0), 0.0)
                            * Math.Max(Math.Min(sb.Y1, e.Y1) - Math.Max(sb.Y0, e.Y0), 0.0);
                        if (ov >= 0.5 * area)
                        {
                            merged = (s, b);
                            break;
                        }
                    }
                }

                if (merged is not (int ms, int mb))
                {
                    break;
                }

                groups[mb].AddRange(groups[ms]);
                groups[mb].Sort();
                groups.RemoveAt(ms);
            }
        }

        if (groups.Count > 0)
        {
            bool[] taken = new bool[n];
            foreach (List<int> g in groups)
            {
                foreach (int i in g)
                {
                    taken[i] = true;
                }
            }

            foreach (List<int> g in groups)
            {
                Box4 b = BBoxOf(units, g);
                for (int i = 0; i < n; i++)
                {
                    if (taken[i] || l.Node(i, 0) < -8.0f)
                    {
                        continue;
                    }

                    Box4 u = units[i].Bx;
                    double cx = (u.X0 + u.X1) / 2.0, cy = (u.Y0 + u.Y1) / 2.0;
                    if (b.X0 <= cx && cx <= b.X1 && b.Y0 <= cy && cy <= b.Y1)
                    {
                        g.Add(i);
                        taken[i] = true;
                    }
                }

                g.Sort();
            }

            if (opt.HeadBand)
            {
                HeadBand(units, l, groups, taken);
            }
        }

        return groups;
    }
}
