// Ported from LiteParse crates/liteparse/src/ocr/ocria/img.rs, Apache-2.0.
namespace OcriaNano.DocumentExtraction.Imaging;

/// <summary>One 4-connected component of a binary mask: its bounding box and pixels (in discovery order).</summary>
internal sealed class Component
{
    public int X0 { get; set; } = int.MaxValue;

    public int Y0 { get; set; } = int.MaxValue;

    public int X1 { get; set; }

    public int Y1 { get; set; }

    public List<(int X, int Y)> Pixels { get; } = [];
}

/// <summary>A rotated rectangle: centre, side lengths along <c>u</c> (W) and its normal (H), and <c>u</c>'s angle in degrees.</summary>
internal readonly record struct RotRect(float Cx, float Cy, float W, float H, float Angle);

/// <summary>Connected components, convex hull / <c>minAreaRect</c>, <c>boxPoints</c> and Otsu, all scalar.</summary>
internal static class Geometry
{
    /// <summary>
    /// 4-connected components of <paramref name="mask"/> (row-major), labelled in raster order of each component's first
    /// pixel, as <c>cv2.connectedComponentsWithStats</c> numbers them.
    /// </summary>
    public static List<Component> Components4(ReadOnlySpan<bool> mask, int w, int h)
    {
        bool[] seen = new bool[w * h];
        bool[] m = mask.ToArray();
        List<Component> output = [];
        Stack<int> stack = new();
        for (int start = 0; start < w * h; start++)
        {
            if (!m[start] || seen[start])
            {
                continue;
            }

            seen[start] = true;
            stack.Push(start);
            Component comp = new();
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                comp.X0 = Math.Min(comp.X0, x);
                comp.Y0 = Math.Min(comp.Y0, y);
                comp.X1 = Math.Max(comp.X1, x);
                comp.Y1 = Math.Max(comp.Y1, y);
                comp.Pixels.Add((x, y));
                if (x > 0)
                {
                    Visit(p - 1);
                }

                if (x + 1 < w)
                {
                    Visit(p + 1);
                }

                if (y > 0)
                {
                    Visit(p - w);
                }

                if (y + 1 < h)
                {
                    Visit(p + w);
                }
            }

            output.Add(comp);
        }

        return output;

        void Visit(int q)
        {
            if (m[q] && !seen[q])
            {
                seen[q] = true;
                stack.Push(q);
            }
        }
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
        => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    /// <summary><c>cv2.minAreaRect</c>: the smallest-area rectangle around the points (convex hull + one candidate per hull edge).</summary>
    public static RotRect MinAreaRect(ReadOnlySpan<(float X, float Y)> pts)
    {
        List<(double X, double Y)> p = [];
        foreach ((float x, float y) in pts)
        {
            p.Add((x, y));
        }

        p.Sort((a, b) =>
        {
            int c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Y.CompareTo(b.Y);
        });
        for (int i = p.Count - 1; i > 0; i--)
        {
            if (p[i] == p[i - 1])
            {
                p.RemoveAt(i);
            }
        }

        if (p.Count == 1)
        {
            return new RotRect((float)p[0].X, (float)p[0].Y, 0f, 0f, 0f);
        }

        // monotone chain
        List<(double X, double Y)> hull = new(p.Count * 2);
        foreach (var q in p)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], q) <= 0.0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(q);
        }

        int lower = hull.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            var q = p[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], q) <= 0.0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(q);
        }

        hull.RemoveAt(hull.Count - 1);

        double bestArea = double.NaN;
        RotRect? best = null;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = double.Hypot(dx, dy);
            if (len == 0.0)
            {
                continue;
            }

            double ux = dx / len, uy = dy / len;
            double vx = -uy, vy = ux;
            double u0 = double.PositiveInfinity, u1 = double.NegativeInfinity, v0 = double.PositiveInfinity, v1 = double.NegativeInfinity;
            foreach (var q in hull)
            {
                double pu = (q.X * ux) + (q.Y * uy);
                double pv = (q.X * vx) + (q.Y * vy);
                u0 = Math.Min(u0, pu);
                u1 = Math.Max(u1, pu);
                v0 = Math.Min(v0, pv);
                v1 = Math.Max(v1, pv);
            }

            double area = (u1 - u0) * (v1 - v0);
            if (best is null || area < bestArea)
            {
                double cu = (u0 + u1) / 2.0, cv = (v0 + v1) / 2.0;
                bestArea = area;
                best = new RotRect(
                    (float)((cu * ux) + (cv * vx)),
                    (float)((cu * uy) + (cv * vy)),
                    (float)(u1 - u0),
                    (float)(v1 - v0),
                    (float)(Math.Atan2(uy, ux) * (180.0 / Math.PI)));
            }
        }

        return best ?? new RotRect((float)p[0].X, (float)p[0].Y, 0f, 0f, 0f);
    }

    /// <summary><c>cv2.boxPoints</c>: the rectangle's four corners.</summary>
    public static (float X, float Y)[] BoxPoints(RotRect r)
    {
        double a = r.Angle * (Math.PI / 180.0);
        float b = (float)(Math.Cos(a) * 0.5), c = (float)(Math.Sin(a) * 0.5);
        (float, float) p0 = (r.Cx - (c * r.H) - (b * r.W), r.Cy + (b * r.H) - (c * r.W));
        (float, float) p1 = (r.Cx + (c * r.H) - (b * r.W), r.Cy - (b * r.H) - (c * r.W));
        (float, float) p2 = ((2f * r.Cx) - p0.Item1, (2f * r.Cy) - p0.Item2);
        (float, float) p3 = ((2f * r.Cx) - p1.Item1, (2f * r.Cy) - p1.Item2);
        return [p0, p1, p2, p3];
    }

    /// <summary>Otsu's threshold over 8-bit values (<c>server._otsu</c>): the first level maximising the between-class variance.</summary>
    public static byte Otsu(IEnumerable<byte> values)
    {
        double[] hist = new double[256];
        double n = 0;
        foreach (byte v in values)
        {
            hist[v] += 1.0;
            n += 1.0;
        }

        n = Math.Max(n, 1.0);
        double w = 0, mu = 0;
        (double W, double Mu)[] cum = new (double, double)[256];
        for (int i = 0; i < 256; i++)
        {
            w += hist[i] / n;
            mu += hist[i] / n * i;
            cum[i] = (w, mu);
        }

        double muT = cum[255].Mu;
        double bestScore = double.NegativeInfinity;
        byte best = 0;
        for (int i = 0; i < 256; i++)
        {
            double t = (muT * cum[i].W) - cum[i].Mu;
            double s = t * t / (cum[i].W * (1.0 - cum[i].W));
            if (double.IsFinite(s) && s > bestScore)
            {
                bestScore = s;
                best = (byte)i;
            }
        }

        return best;
    }
}
