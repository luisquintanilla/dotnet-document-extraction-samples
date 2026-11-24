// Ported from LiteParse crates/liteparse/src/table_model/mod.rs (block_groups, split_at_gaps, split_paragraphs, block assembly in run_page_framed), Apache-2.0.
namespace OcriaNano.DocumentExtraction.Layout;

internal static class BlockDecoder
{
    /// <summary>Units grouped into blocks from same_block logits: connected components of positive links, then average linkage inside each component.</summary>
    internal static List<List<int>> BlockGroups(Logits l, IReadOnlyList<int> keep)
    {
        int n = l.N;
        int m = keep.Count;
        int[] parent = [.. Enumerable.Range(0, m)];
        static int Root(int[] p, int k)
        {
            while (p[k] != k)
            {
                p[k] = p[p[k]];
                k = p[k];
            }

            return k;
        }

        for (int a = 0; a < m; a++)
        {
            for (int b = a + 1; b < m; b++)
            {
                if (l.PairData[(((4 * n) + keep[a]) * n) + keep[b]] > 0f)
                {
                    int ra = Root(parent, a), rb = Root(parent, b);
                    if (ra != rb)
                    {
                        parent[ra] = rb;
                    }
                }
            }
        }

        SortedDictionary<int, List<int>> comps = [];
        for (int a = 0; a < m; a++)
        {
            int r = Root(parent, a);
            if (!comps.TryGetValue(r, out List<int>? list))
            {
                comps[r] = list = [];
            }

            list.Add(a);
        }

        List<List<int>> output = [];
        foreach (List<int> c in comps.Values)
        {
            int k = c.Count;
            double[] p = new double[k * k];
            for (int x = 0; x < k; x++)
            {
                for (int y = 0; y < k; y++)
                {
                    p[(x * k) + y] = l.PairData[(((4 * n) + keep[c[x]]) * n) + keep[c[y]]];
                }
            }

            foreach (List<int> g in AvgLinkage.AvgLink(p, k, 0.0))
            {
                output.Add([.. g.Select(x => keep[c[x]])]);
            }
        }

        return output;
    }

    /// <summary>A block's units split where their horizontal extents leave a gap wider than the units' median height.</summary>
    internal static List<List<int>> SplitAtGaps(List<int> g, IReadOnlyList<Unit> units)
    {
        List<double> hs = [.. g.Select(i => units[i].PBox.Y1 - units[i].PBox.Y0)];
        hs.Sort();
        double gap = Math.Max(hs.Count > 0 ? hs[hs.Count / 2] : 0.0, 1.0);
        List<int> byX = [.. g];
        Units.StableSort(byX, (a, b) => units[a].PBox.X0.CompareTo(units[b].PBox.X0));
        List<List<int>> parts = [];
        double right = double.NegativeInfinity;
        foreach (int i in byX)
        {
            Box4 p = units[i].PBox;
            if (parts.Count == 0 || p.X0 - right > gap)
            {
                parts.Add([]);
            }

            parts[^1].Add(i);
            right = Math.Max(right, p.X1);
        }

        return parts;
    }

    /// <summary>A block's units split into line runs at paragraph breaks and heading / body changes.</summary>
    internal static List<List<int>> SplitParagraphs(List<int> g, IReadOnlyList<Unit> units, float[] cls, int c)
    {
        if (g.Count < 2 || c < 3)
        {
            return [[.. g]];
        }

        List<int> byY = [.. g];
        Units.StableSort(byY, (a, b) => ((units[a].PBox.Y0 + units[a].PBox.Y1) / 2.0).CompareTo((units[b].PBox.Y0 + units[b].PBox.Y1) / 2.0));
        List<(double Y0, double Y1, List<int> Ks)> lines = [];
        foreach (int i in byY)
        {
            Box4 p = units[i].PBox;
            if (lines.Count > 0)
            {
                (double y0, double y1, List<int> ks) = lines[^1];
                if (Math.Min(y1, p.Y1) - Math.Max(y0, p.Y0) > 0.5 * Math.Min(y1 - y0, p.Y1 - p.Y0))
                {
                    ks.Add(i);
                    lines[^1] = (Math.Min(y0, p.Y0), Math.Max(y1, p.Y1), ks);
                    continue;
                }
            }

            lines.Add((p.Y0, p.Y1, [i]));
        }

        if (lines.Count < 2)
        {
            return [[.. g]];
        }

        List<double> hs = [.. lines.Select(x => x.Y1 - x.Y0)];
        hs.Sort();
        double lineH = Math.Max(hs[hs.Count / 2], 1.0);

        bool Headingish(List<int> l)
        {
            float head = 0f, all = 0f;
            foreach (int i in l)
            {
                ReadOnlySpan<float> row = cls.AsSpan(i * c, c);
                float m = float.NegativeInfinity;
                foreach (float v in row)
                {
                    m = MathF.Max(m, v);
                }

                float[] e = new float[c];
                float z = 0f;
                for (int k = 0; k < c; k++)
                {
                    e[k] = MathF.Exp(row[k] - m);
                    z += e[k];
                }

                float n = Math.Max(units[i].Text.EnumerateRunes().Count(r => !System.Text.Rune.IsWhiteSpace(r)), 1);
                head += n * (e[1] + e[2]) / z;
                all += n;
            }

            return head * 2f > all;
        }

        List<List<int>> parts = [[.. lines[0].Ks]];
        for (int k = 1; k < lines.Count; k++)
        {
            double gap = lines[k].Y0 - lines[k - 1].Y1;
            if (gap > lineH || Headingish(lines[k].Ks) != Headingish(lines[k - 1].Ks))
            {
                parts.Add([]);
            }

            parts[^1].AddRange(lines[k].Ks);
        }

        return parts;
    }

    private static int ArgMaxLast(float[] sum, int fallback)
    {
        if (sum.Length == 0)
        {
            return fallback;
        }

        int best = 0;
        for (int k = 1; k < sum.Length; k++)
        {
            if (sum[k] >= sum[best])
            {
                best = k;
            }
        }

        return best;
    }

    /// <summary>Layout blocks of a page (units in the reading frame); boxes are in that frame too.</summary>
    public static List<ModelBlock> Build(IReadOnlyList<Unit> units, Logits cl)
    {
        List<ModelBlock> blocks = [];
        if (cl.NPair != 5 || cl.NCls <= 0)
        {
            return blocks;
        }

        int c = cl.NCls;
        List<int> keep = [.. Enumerable.Range(0, units.Count).Where(i => !units[i].Minor)];
        foreach (List<int> g0 in BlockGroups(cl, keep))
        {
            List<List<int>> parts = [.. SplitAtGaps(g0, units).SelectMany(part => SplitParagraphs(part, units, cl.Cls, c))];
            foreach (List<int> g in parts)
            {
                float[] s = new float[c];
                foreach (int i in g)
                {
                    for (int k = 0; k < c; k++)
                    {
                        s[k] += cl.Cls[(i * c) + k];
                    }
                }

                int cls = ArgMaxLast(s, 0);
                List<Box4> boxes = [.. g.Select(i => units[i].Bx)];
                string text = string.Join(" ", ReadingOrder.Order(boxes).Select(k => units[g[k]].Text));
                Box4 b = g.Aggregate(Box4.Empty, (acc, i) => acc.Union(units[i].PBox));
                blocks.Add(new ModelBlock(b, (LayoutClass)cls, text, g));
            }
        }

        return blocks;
    }
}
