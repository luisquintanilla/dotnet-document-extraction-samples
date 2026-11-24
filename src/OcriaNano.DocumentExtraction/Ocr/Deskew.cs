// Ported from LiteParse crates/liteparse/src/ocr_merge.rs (ocr_page_skew, rotate_about) and stages.rs
// (rotate_back_ocr_deskew), Apache-2.0.
namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// Deskews a tilted scan. When all of a page's text is OCR, its lines are turned upright about the page centre before
/// layout, because the axis-aligned boxes of tilted lines overlap the lines above and below and get merged and
/// reordered. Output geometry is turned back onto the page image afterwards.
/// </summary>
internal static class Deskew
{
    /// <summary>Below this tilt rows already group correctly.</summary>
    public const double MinDegrees = 0.3;

    /// <summary>Above this tilt the "tilt" is more likely rotated or curved text than a skewed scan.</summary>
    public const double MaxDegrees = 10.0;

    /// <summary>
    /// The page's tilt in degrees (y down: positive means lines descend to the right), as the length-weighted median
    /// angle of its long, near-horizontal lines (length at least 4x height). Null below three such lines or outside
    /// the deskew range.
    /// </summary>
    public static double? PageSkew(IReadOnlyList<OcrLine> lines)
    {
        List<(double Deg, double Len)> longLines = [];
        foreach (OcrLine l in lines)
        {
            if (l.Quad is not { Length: 4 } p)
            {
                continue;
            }

            double dx = p[1].X - p[0].X, dy = p[1].Y - p[0].Y;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double h = Math.Sqrt(Math.Pow(p[3].X - p[0].X, 2) + Math.Pow(p[3].Y - p[0].Y, 2));
            double deg = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (len >= 4 * h && len > 0 && Math.Abs(deg) <= 45)
            {
                longLines.Add((deg, len));
            }
        }

        if (longLines.Count < 3)
        {
            return null;
        }

        longLines.Sort((a, b) => a.Deg.CompareTo(b.Deg));
        double half = longLines.Sum(l => l.Len) / 2, acc = 0, mid = 0;
        foreach ((double deg, double len) in longLines)
        {
            acc += len;
            if (acc >= half)
            {
                mid = deg;
                break;
            }
        }

        return Math.Abs(mid) is >= MinDegrees and <= MaxDegrees ? mid : null;
    }

    /// <summary>Rotates (x, y) by <paramref name="degrees"/> about (cx, cy) in a y-down frame.</summary>
    public static (double X, double Y) RotateAbout(double x, double y, double cx, double cy, double degrees)
    {
        (double s, double c) = Math.SinCos(degrees * Math.PI / 180);
        double dx = x - cx, dy = y - cy;
        return (cx + (dx * c) - (dy * s), cy + (dx * s) + (dy * c));
    }

    /// <summary>
    /// The line turned upright (rotated by -<paramref name="degrees"/> about (cx, cy), in image pixels): its quad
    /// rotates and its box becomes the quad's bounds; a line without a quad moves its box by its centre.
    /// </summary>
    public static OcrLine Upright(OcrLine line, double cx, double cy, double degrees)
    {
        if (line.Quad is { Length: 4 } q)
        {
            PointF[] pts = [.. q.Select(p => Turn(p.X, p.Y))];
            return line with { Quad = pts, Box = BoxF.Bounds(pts) };
        }

        BoxF b = line.Box;
        PointF m = Turn(b.CenterX, b.CenterY);
        float hw = b.Width / 2, hh = b.Height / 2;
        return line with { Box = new BoxF(m.X - hw, m.Y - hh, m.X + hw, m.Y + hh) };

        PointF Turn(double x, double y)
        {
            (double ux, double uy) = RotateAbout(x, y, cx, cy, -degrees);
            return new PointF((float)ux, (float)uy);
        }
    }

    /// <summary>The four corners of an upright rectangle turned back onto the tilted page.</summary>
    public static IReadOnlyList<(double X, double Y)> RotateBack(
        IReadOnlyList<(double X, double Y)> polygon, double cx, double cy, double degrees)
        => [.. polygon.Select(p => RotateAbout(p.X, p.Y, cx, cy, degrees))];
}
