// Ported from LiteParse crates/liteparse/src/table_model/decode.rs (#[cfg(test)]), Apache-2.0.
using OcriaNano.DocumentExtraction.Layout;

namespace OcriaNano.DocumentExtraction.Tests.Layout;

public class DecodeTests
{
    private static List<List<int>> Reference(double[] p, int n, double thresh)
    {
        List<int>[] groups = [.. Enumerable.Range(0, n).Select(i => new List<int> { i })];
        double[] s = (double[])p.Clone();
        double[] cnt = new double[n * n];
        Array.Fill(cnt, 1.0);
        List<int> alive = [.. Enumerable.Range(0, n)];
        while (alive.Count > 1)
        {
            double best = double.NegativeInfinity;
            int ba = 0, bb = 0;
            for (int a = 0; a < alive.Count; a++)
            {
                for (int b = 0; b < alive.Count; b++)
                {
                    if (a == b)
                    {
                        continue;
                    }

                    int ga0 = alive[a], gb0 = alive[b];
                    double v = s[(ga0 * n) + gb0] / cnt[(ga0 * n) + gb0];
                    if (v > best)
                    {
                        best = v;
                        ba = a;
                        bb = b;
                    }
                }
            }

            if (best <= thresh)
            {
                break;
            }

            int ga = alive[ba], gb = alive[bb];
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
        }

        return [.. alive.Select(g => groups[g].Order().ToList())];
    }

    [Fact]
    public void AvgLinkMatchesReference()
    {
        ulong x = 0x9E37_79B9_7F4A_7C15;
        ulong Next()
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            return x;
        }

        double[] levels = [-2.0, -1.0, -0.5, 0.0, 0.25, 0.5, 1.0, 2.0];
        for (int c = 0; c < 400; c++)
        {
            int n = 1 + (int)(Next() % 80);
            bool symmetric = c % 4 != 3;
            bool discrete = c % 2 == 0;
            double[] p = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (symmetric && j < i)
                    {
                        p[(i * n) + j] = p[(j * n) + i];
                        continue;
                    }

                    p[(i * n) + j] = discrete ? levels[(int)(Next() % (ulong)levels.Length)] : (Next() % 20_001 / 5_000.0) - 2.0;
                }
            }

            foreach (double thresh in new[] { 0.0, -0.75, 0.6 })
            {
                var expected = Reference(p, n, thresh);
                var actual = AvgLinkage.AvgLink(p, n, thresh);
                Assert.True(
                    expected.Count == actual.Count && expected.Zip(actual).All(t => t.First.SequenceEqual(t.Second)),
                    $"case {c} n {n} symmetric {symmetric} discrete {discrete} thresh {thresh}");
            }
        }
    }

    private static PlacedCell HCell(int r, int c, string text, double y0, double y1) => new()
    {
        R0 = r,
        C0 = c,
        R1 = r,
        C1 = c,
        Text = text,
        Bx = new Box4(c * 100.0, y0, (c * 100.0) + 90.0, y1),
    };

    [Fact]
    public void WrappedHeaderLinesFold()
    {
        List<PlacedCell> cells =
        [
            HCell(0, 0, "Product", 76.6, 83.6),
            HCell(0, 1, "Date", 76.6, 83.6),
            HCell(0, 2, "Recommendation", 76.6, 83.6),
            HCell(1, 0, "(active substance(s))", 87.5, 96.2),
            HCell(1, 2, "(additions to text)", 87.5, 96.2),
            HCell(2, 0, "Purevax", 110, 118),
            HCell(2, 1, "5-7 October 2021", 110, 118),
            HCell(2, 2, "Section 4.6", 110, 118),
        ];
        List<int> head = [0];
        int n = 3;
        GridDecoder.FoldHeaderWraps(cells, head, ref n);
        Assert.Equal(2, n);
        Assert.Contains(cells, c => c.Text == "Product (active substance(s))");
    }

    [Fact]
    public void NameListsAndLoneTailsDoNotFold()
    {
        List<PlacedCell> list =
        [
            HCell(0, 0, "Alfa Romeo", 10, 18),
            HCell(0, 1, "Audi", 10, 18),
            HCell(1, 0, "Buick Reatta", 20, 28),
            HCell(1, 1, "Cadillac", 20, 28),
        ];
        List<int> head = [];
        int n = 2;
        GridDecoder.FoldHeaderWraps(list, head, ref n);
        Assert.Equal(2, n);

        List<PlacedCell> tail =
        [
            HCell(0, 0, "Country", 10, 18),
            HCell(0, 1, "Sector", 10, 18),
            HCell(0, 2, "Restrictions", 10, 18),
            HCell(1, 2, "right required to acquire land", 20, 28),
        ];
        head = [0];
        n = 2;
        GridDecoder.FoldHeaderWraps(tail, head, ref n);
        Assert.Equal(2, n);
    }
}
