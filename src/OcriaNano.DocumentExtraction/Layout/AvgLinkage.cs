// Ported from LiteParse crates/liteparse/src/table_model/{decode.rs,net.rs} (DUnit, DecodeOptions, Logits, avg_link, reading_order), Apache-2.0.
namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>A text unit as the decoder sees it: box in the text's reading frame (points, top-left origin).</summary>
internal readonly record struct DUnit(Box4 Bx, string Text, double H);

/// <summary>Decoder switches (the shipped defaults).</summary>
internal sealed record DecodeOptions
{
    public float TableThresh { get; init; } = 0f;

    public bool HeadBand { get; init; } = true;

    public bool ForcedLink { get; init; } = true;

    public bool Squeeze { get; init; } = true;

    /// <summary>Fold wrapped-cell continuation rows into the row above (not in tn_core).</summary>
    public bool FoldWraps { get; init; } = true;
}

/// <summary>Node logits [n, 2], pair logits [nPair, n, n] (row-major) and block class logits [n, nCls] (empty without a class head).</summary>
internal sealed class Logits
{
    public Logits(int n, float[] node, float[] pair, float[] cls, int nCls, int nPair)
    {
        N = n;
        NodeData = node;
        PairData = pair;
        Cls = cls;
        NCls = nCls;
        NPair = nPair;
    }

    public int N { get; }

    public float[] NodeData { get; }

    public float[] PairData { get; }

    public float[] Cls { get; }

    public int NCls { get; }

    /// <summary>Pair heads present: 4, or 5 with same_block.</summary>
    public int NPair { get; }

    public float Node(int i, int k) => NodeData[(i * 2) + k];

    public float Pair(int k, int i, int j) => PairData[(((k * N) + i) * N) + j];
}

internal static class AvgLinkage
{
    /// <summary>
    /// Average-linkage clusters of a symmetric n x n matrix: merge while the mean logit exceeds <paramref name="thresh"/>.
    /// Groups come back sorted, in the order of their surviving representative. Ties go to the first pair in row-major
    /// order of the live groups; each live group caches its best partner.
    /// </summary>
    public static List<List<int>> AvgLink(double[] p, int n, double thresh)
    {
        List<int>[] groups = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            groups[i] = [i];
        }

        double[] s = (double[])p.Clone();
        double[] cnt = new double[n * n];
        Array.Fill(cnt, 1.0);
        List<int> alive = [.. Enumerable.Range(0, n)];

        (double, int) RowBest(int g)
        {
            (double, int) best = (double.NegativeInfinity, int.MaxValue);
            foreach (int h in alive)
            {
                if (h != g)
                {
                    double v = s[(g * n) + h] / cnt[(g * n) + h];
                    if (v > best.Item1)
                    {
                        best = (v, h);
                    }
                }
            }

            return best;
        }

        (double, int)[] best = new (double, int)[n];
        for (int g = 0; g < n; g++)
        {
            best[g] = RowBest(g);
        }

        while (alive.Count > 1)
        {
            int ba = 0;
            for (int a = 1; a < alive.Count; a++)
            {
                if (best[alive[a]].Item1 > best[alive[ba]].Item1)
                {
                    ba = a;
                }
            }

            (double v, int gb) = best[alive[ba]];
            if (v <= thresh)
            {
                break;
            }

            int ga = alive[ba];
            int bb = alive.IndexOf(gb);
            groups[ga].AddRange(groups[gb]);
            groups[gb] = [];
            for (int k = 0; k < n; k++)
            {
                s[(ga * n) + k] += s[(gb * n) + k];
                cnt[(ga * n) + k] += cnt[(gb * n) + k];
            }

            for (int k = 0; k < n; k++)
            {
                s[(k * n) + ga] += s[(k * n) + gb];
                cnt[(k * n) + ga] += cnt[(k * n) + gb];
            }

            alive.RemoveAt(bb);
            best[ga] = RowBest(ga);
            foreach (int r in alive)
            {
                if (r == ga)
                {
                    continue;
                }

                (double bv, int bc) = best[r];
                if (bc == ga || bc == gb)
                {
                    best[r] = RowBest(r);
                }
                else
                {
                    double c = s[(r * n) + ga] / cnt[(r * n) + ga];
                    if (c > bv || (c == bv && ga < bc))
                    {
                        best[r] = (c, ga);
                    }
                }
            }
        }

        List<List<int>> result = [];
        foreach (int g in alive)
        {
            List<int> v = [.. groups[g]];
            v.Sort();
            result.Add(v);
        }

        return result;
    }
}

internal static class ReadingOrder
{
    /// <summary>Box indices in reading order: lines (vertical overlap over half the shorter box), top to bottom, left to right within a line.</summary>
    public static List<int> Order(IReadOnlyList<Box4> boxes)
    {
        List<int> order = [.. Enumerable.Range(0, boxes.Count)];
        Units.StableSort(order, (a, b) => boxes[a].Y0.CompareTo(boxes[b].Y0));
        List<(double Y0, double Y1, List<int> Ks)> lines = [];
        foreach (int k in order)
        {
            Box4 b = boxes[k];
            bool placed = false;
            for (int li = 0; li < lines.Count; li++)
            {
                (double y0, double y1, List<int> ks) = lines[li];
                double ov = Math.Min(y1, b.Y1) - Math.Max(y0, b.Y0);
                if (ov > 0.5 * Math.Min(y1 - y0, b.Y1 - b.Y0))
                {
                    ks.Add(k);
                    lines[li] = (Math.Min(y0, b.Y0), Math.Max(y1, b.Y1), ks);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                lines.Add((b.Y0, b.Y1, [k]));
            }
        }

        Units.StableSort(lines, (a, b) => a.Y0.CompareTo(b.Y0));
        List<int> output = [];
        foreach ((_, _, List<int> ks) in lines)
        {
            Units.StableSort(ks, (a, b) => boxes[a].X0.CompareTo(boxes[b].X0));
            output.AddRange(ks);
        }

        return output;
    }
}
