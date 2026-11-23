// Ported from LiteParse crates/liteparse/src/ocr/ocria/page.rs, Apache-2.0.
using System.Text;
using OcriaNano.DocumentExtraction.Imaging;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// Page-level steps around the models (<c>server.py</c>): line crops, column-gutter splitting, orientation,
/// reading-order quads, and the reading filters.
/// </summary>
internal static class PageFrame
{
    // Minimum blank-run width, in line heights, for a column gutter.
    private const double GutterMin = 0.4;

    private static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Perspective-crop a (possibly rotated) quad, as PaddleOCR does (<c>lpocr_rec._crop</c>).</summary>
    public static Img Crop(Img img, PointF[] q)
    {
        int w = Math.Max((int)MathF.Max(Dist(q[0], q[1]), Dist(q[2], q[3])), 2);
        int h = Math.Max((int)MathF.Max(Dist(q[0], q[3]), Dist(q[1], q[2])), 2);
        PointF[] dst = [new(0f, 0f), new(w, 0f), new(w, h), new(0f, h)];
        double[] m = Perspective.Transform(q, dst);
        Img c = Perspective.WarpCubic(img, m, w, h);

        // vertical box: turn it horizontal
        return h / (double)w >= 1.5 ? c.Rot90(1) : c;
    }

    private static (double X0, double Y0, double X1, double Y1) Extent(PointF[] q)
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (PointF p in q)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }

        return (x0, y0, x1, y1);
    }

    /// <summary>Boxes that are clearly horizontal text lines (at least 3x wider than tall, &gt;= 30 px).</summary>
    public static int LongLines(IReadOnlyList<PointF[]> boxes)
    {
        int count = 0;
        foreach (PointF[] q in boxes)
        {
            (double x0, double y0, double x1, double y1) = Extent(q);
            double dx = x1 - x0, dy = y1 - y0;
            if (dx >= 30.0 && dx >= 3.0 * dy)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Detector boxes of a page scanned on its side: many boxes, almost none horizontal lines.</summary>
    public static bool LooksSideways(IReadOnlyList<PointF[]> boxes)
        => boxes.Count >= 15 && LongLines(boxes) < 0.25 * boxes.Count;

    /// <summary>
    /// A box quad (screen order in the image it was read in) as a quad in the original image whose corners run in
    /// reading order: upside-down pages swap corners; pages read after a quarter turn map back with x = w0 - 1 - y', y = x'.
    /// </summary>
    public static PointF[] ReadingFrame(PointF[] q, bool flip, bool sideways, int w0)
    {
        PointF[] p = [.. q];
        if (flip)
        {
            p = [p[2], p[3], p[0], p[1]];
        }

        if (sideways)
        {
            for (int i = 0; i < p.Length; i++)
            {
                p[i] = new PointF((float)(w0 - 1.0 - p[i].Y), p[i].X);
            }
        }

        return p;
    }

    /// <summary>
    /// Cut near-horizontal boxes at blank vertical runs that line up with other boxes' edges and stay clear in the
    /// neighbouring lines (<c>server.split_gutters</c>).
    /// </summary>
    public static List<PointF[]> SplitGutters(Img img, IReadOnlyList<PointF[]> boxes)
    {
        if (boxes.Count < 3)
        {
            return [.. boxes];
        }

        Img gray = img.MeanGray();
        long gw = gray.Width, gh = gray.Height;
        byte Px(long x, long y) => gray.Data[(y * gw) + x];
        var ext = new (double X0, double Y0, double X1, double Y1)[boxes.Count];
        for (int i = 0; i < ext.Length; i++)
        {
            ext[i] = Extent(boxes[i]);
        }

        List<PointF[]> output = new(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
        {
            PointF[] q = boxes[i];
            (double x0, double y0, double x1, double y1) = ext[i];
            double h = y1 - y0;
            double tilt = Math.Abs((double)q[1].Y - q[0].Y);
            if (h < 6.0 || x1 - x0 < 4.0 * h || tilt > 0.25 * h)
            {
                output.Add(q);
                continue;
            }

            long ya = Math.Min(Math.Max((long)(y0 + (0.15 * h)), 0), gh);
            long yb = Math.Clamp((long)Math.Ceiling(y1 - (0.15 * h)), 0, gh);
            long xa = Math.Max((long)x0, 0);
            long xb = Math.Min((long)Math.Ceiling(x1), gw);
            if (yb <= ya || xb <= xa)
            {
                output.Add(q);
                continue;
            }

            byte lo = 255, hi = 0;
            for (long y = ya; y < yb; y++)
            {
                for (long x = xa; x < xb; x++)
                {
                    byte v = Px(x, y);
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
            }

            if (hi - lo < 60)
            {
                output.Add(q);
                continue;
            }

            byte thr = Geometry.Otsu(Region(gray, xa, xb, ya, yb));

            // A printed column rule (a thin solid stroke that carries on above and below the line, through the space
            // between lines) belongs to the gutter, not the text: glyph stems stop at the line.
            HashSet<long> rule = RuleColumns(Px, (xa, xb), (y0, y1), (gw, gh), thr);

            // ink columns (relative to xa)
            List<long> idx = [];
            for (long x = xa; x < xb; x++)
            {
                if (rule.Contains(x))
                {
                    continue;
                }

                for (long y = ya; y < yb; y++)
                {
                    if (Px(x, y) < thr)
                    {
                        idx.Add(x - xa);
                        break;
                    }
                }
            }

            if (idx.Count < 2)
            {
                output.Add(q);
                continue;
            }

            double cy = (y0 + y1) / 2.0, tol = 0.6 * h;
            List<(double A, double B)> cuts = [];
            for (int g = 0; g < idx.Count - 1; g++)
            {
                if ((idx[g + 1] - idx[g]) <= GutterMin * h)
                {
                    continue;
                }

                // blank columns [a, b)
                double a = xa + idx[g] + 1, b = xa + idx[g + 1];
                int left = 0, right = 0;
                for (int j = 0; j < ext.Length; j++)
                {
                    var e = ext[j];
                    if (j != i && Math.Abs(((e.Y0 + e.Y1) / 2.0) - cy) < 25.0 * h && 0.5 * h < e.Y1 - e.Y0 && e.Y1 - e.Y0 < 2.0 * h)
                    {
                        left += Math.Abs(e.X0 - b) < tol ? 1 : 0;
                        right += Math.Abs(e.X1 - a) < tol ? 1 : 0;
                    }
                }

                // both sides need a column of edges; a hanging-indent marker has only one
                if (left < 1 || right < 1 || left + right < 3 || a - x0 < 3.0 * h)
                {
                    continue;
                }

                // the gutter stays clear in the neighbouring lines, judged per line
                double m = Math.Max((long)((b - a) * 0.25), 1);
                long sa = (long)(a + m), sb = (long)(b - m);
                if (sb <= sa)
                {
                    continue;
                }

                int ok = 0, bad = 0;
                for (int j = 0; j < ext.Length; j++)
                {
                    var e = ext[j];
                    if (j == i || Math.Abs(((e.Y0 + e.Y1) / 2.0) - cy) > 4.0 * h || !(0.5 * h < e.Y1 - e.Y0 && e.Y1 - e.Y0 < 2.0 * h))
                    {
                        continue;
                    }

                    if (e.X1 <= sa || e.X0 >= sb)
                    {
                        ok++;
                        continue;
                    }

                    double eh = e.Y1 - e.Y0;
                    long ra = Math.Clamp((long)(e.Y0 + (0.15 * eh)), 0, gh);
                    long rb = Math.Clamp((long)Math.Ceiling(e.Y1 - (0.15 * eh)), 0, gh);
                    long ca = Math.Clamp(sa, 0, gw), cb = Math.Clamp(sb, 0, gw);
                    bool inked = false;
                    for (long y = ra; y < rb && !inked; y++)
                    {
                        for (long x = ca; x < cb; x++)
                        {
                            if (!rule.Contains(x) && Px(x, y) < thr)
                            {
                                inked = true;
                                break;
                            }
                        }
                    }

                    if (inked)
                    {
                        bad++;
                    }
                    else
                    {
                        ok++;
                    }
                }

                if (!(ok >= 2 && (bad == 0 || (bad == 1 && ok >= 3))))
                {
                    continue;
                }

                cuts.Add((a, b));
            }

            if (cuts.Count == 0)
            {
                output.Add(q);
                continue;
            }

            double pad = 0.15 * h;
            List<double> edges = [x0];
            foreach ((double a, double b) in cuts)
            {
                edges.Add(a + pad);
                edges.Add(b - pad);
            }

            edges.Add(x1);
            for (int k = 0; k + 1 < edges.Count; k += 2)
            {
                double s = edges[k], e = edges[k + 1];
                if (e - s > 0.5 * h)
                {
                    float fs = (float)s, fe = (float)e, fya = (float)y0, fyb = (float)y1;
                    output.Add([new(fs, fya), new(fe, fya), new(fe, fyb), new(fs, fyb)]);
                }
            }
        }

        return output;
    }

    private static IEnumerable<byte> Region(Img gray, long xa, long xb, long ya, long yb)
    {
        for (long y = ya; y < yb; y++)
        {
            for (long x = xa; x < xb; x++)
            {
                yield return gray.Data[(y * gray.Width) + x];
            }
        }
    }

    /// <summary>
    /// Columns of a printed vertical rule crossing the line box <c>y0..y1</c>: runs at most ~0.15 line heights wide that
    /// are inked down the whole line and through bands of 0.35 line heights above and below it, with blank columns on
    /// both sides, widened by their anti-aliased edges.
    /// </summary>
    private static HashSet<long> RuleColumns(
        Func<long, long, byte> px,
        (long Xa, long Xb) xs,
        (double Y0, double Y1) ys,
        (long Gw, long Gh) size,
        byte thr)
    {
        (long xa, long xb) = xs;
        (double y0, double y1) = ys;
        (long gw, long gh) = size;
        double h = y1 - y0;
        long band = (long)Math.Ceiling(0.35 * h);
        long top = (long)y0, bot = (long)Math.Ceiling(y1);

        double InkedShare(long x, long ya, long yb)
        {
            ya = Math.Clamp(ya, 0, gh);
            yb = Math.Clamp(yb, 0, gh);
            if (yb <= ya || x < 0 || x >= gw)
            {
                return 0.0;
            }

            long n = 0;
            for (long y = ya; y < yb; y++)
            {
                if (px(x, y) < thr)
                {
                    n++;
                }
            }

            return n / (double)(yb - ya);
        }

        bool[] solid = new bool[xb - xa];
        for (long x = xa; x < xb; x++)
        {
            solid[x - xa] = InkedShare(x, top, bot) >= 0.9
                && InkedShare(x, top - band, top) >= 0.8
                && InkedShare(x, bot, bot + band) >= 0.8;
        }

        long maxW = Math.Max((long)Math.Ceiling(0.15 * h), 3);
        HashSet<long> output = [];
        int i = 0;
        while (i < solid.Length)
        {
            if (!solid[i])
            {
                i++;
                continue;
            }

            int start = i;
            while (i < solid.Length && solid[i])
            {
                i++;
            }

            // a stroke between blank columns (a light ground): on a dark panel every gap between light letters is
            // "inked" top to bottom too
            long flank = Math.Max((long)Math.Ceiling(0.25 * h), 3);
            bool BlankSide(long from, long to)
            {
                long n = 0;
                for (long x = from; x < to; x++)
                {
                    if (InkedShare(x, top, bot) < 0.1)
                    {
                        n++;
                    }
                }

                return n * 2 >= to - from;
            }

            long rs = xa + start, re = xa + i;
            if (i - start <= maxW && BlankSide(rs - flank, rs) && BlankSide(re, re + flank))
            {
                // with its anti-aliased edges, inked in only part of the line
                long pad = Math.Max((long)Math.Ceiling(0.06 * h), 2);
                for (long x = xa + start - pad; x < xa + i + pad; x++)
                {
                    output.Add(x);
                }
            }
        }

        return output;
    }

    /// <summary>The most common group (<c>np.bincount(..).argmax()</c>: the lowest index on ties).</summary>
    public static int Majority(IReadOnlyList<int> groups)
    {
        int[] n = new int[4];
        foreach (int g in groups)
        {
            n[g]++;
        }

        int best = 0;
        for (int i = 1; i < 4; i++)
        {
            if (n[i] > n[best])
            {
                best = i;
            }
        }

        return best;
    }

    private static readonly (int, int)[] s_cjk = [(0x3040, 0x30ff), (0x3400, 0x9fff), (0xac00, 0xd7af), (0xf900, 0xfaff)];
    private static readonly (int, int)[] s_rtl = [(0x0590, 0x08ff), (0xfb1d, 0xfdff), (0xfe70, 0xfeff)];

    private static readonly (int, int)[] s_world =
    [
        (0x0900, 0x0dff), (0x0e00, 0x0eff), (0x0f00, 0x109f), (0x10a0, 0x10ff), (0x0530, 0x058f), (0x1200, 0x139f), (0x1780, 0x17ff),
    ];

    /// <summary>Characters that show a reading really is in <paramref name="group"/>'s script (<c>server.SCRIPT_CHARS</c>).</summary>
    public static int ScriptChars(int group, string text)
    {
        int count = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            int u = r.Value;
            bool hit = group switch
            {
                0 => (u < 128 && char.IsAsciiLetter((char)u)) || (u >= 0x00c0 && u <= 0x024f) || (u >= 0x0370 && u <= 0x04ff),
                1 => InAny(s_cjk, u),
                2 => InAny(s_rtl, u),
                _ => InAny(s_world, u),
            };
            if (hit)
            {
                count++;
            }
        }

        return count;

        static bool InAny((int, int)[] ranges, int u)
        {
            foreach ((int a, int b) in ranges)
            {
                if (u >= a && u <= b)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A detector false positive on a picture: no letter or digit, or 1-2 unsure characters.</summary>
    public static bool IsJunk(string text, float confidence)
    {
        const float JunkConfidence = 0.8f;
        int word = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r) || Rune.GetUnicodeCategory(r) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.LetterNumber or System.Globalization.UnicodeCategory.OtherNumber)
            {
                word++;
            }
        }

        return word == 0 || (word <= 2 && confidence < JunkConfidence);
    }
}
