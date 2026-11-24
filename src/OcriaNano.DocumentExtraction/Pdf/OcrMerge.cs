// Ported from LiteParse crates/liteparse/src/ocr_merge.rs (merge_ocr_outcomes without deskew / upright_rotated_scan,
// canonical_quarter, polygon_rotation_deg, is_rule_artifact, native_width_coverage, overlaps_existing_text,
// clean_ocr_table_artifacts, uncovered_path_area), Apache-2.0.
using OcriaNano.DocumentExtraction.Ocr;

namespace OcriaNano.DocumentExtraction.Pdf;

/// <summary>An OCR line the merge accepted, with its cleaned text and its item in page points.</summary>
internal sealed record AcceptedOcrLine(OcrLine Line, TextItem Item);

/// <summary>The outcome of merging a page's OCR lines into its native text.</summary>
internal sealed record OcrMergeResult(List<TextItem> KeptNatives, List<AcceptedOcrLine> Ocr, List<TextItem> DroppedNatives)
{
    /// <summary>Kept native items followed by OCR items.</summary>
    public IEnumerable<TextItem> Items => KeptNatives.Concat(Ocr.Select(o => o.Item));
}

/// <summary>Merges OCR lines into a page's native text items.</summary>
internal static class OcrMerge
{
    private const double RuleArtifactMaxAspect = 0.35;
    private const double PartialNativeCoverage = 0.5;
    private const double OverlapTolerance = 2.0;

    /// <summary>
    /// Keeps the trusted native items and adds OCR lines that don't repeat them. <paramref name="pointsPerPixel"/> scales
    /// OCR pixels to page points (72 / render DPI).
    /// </summary>
    public static OcrMergeResult Merge(
        IReadOnlyList<TextItem> natives, IReadOnlyList<OcrLine> lines, double pointsPerPixel, bool hasNativeText, bool replaceNative)
    {
        GarbledScope scope = replaceNative ? GarbledScope.WholePage : Garble.Scope(natives);
        bool partial = scope.Kind == GarbledKind.Fonts;
        List<TextItem> kept = [];
        List<TextItem> dropped = [];
        foreach (TextItem it in natives)
        {
            bool keep = scope.Kind switch
            {
                GarbledKind.WholePage => false,
                GarbledKind.Fonts => !scope.Fonts.Contains(it.FontName) && !Garble.IsUnusableNative(it),
                _ => !Garble.IsUnusableNative(it),
            };
            (keep ? kept : dropped).Add(it);
        }

        bool[] superseded = new bool[kept.Count];
        List<AcceptedOcrLine> ocr = [];
        double s = pointsPerPixel;
        foreach (OcrLine r in lines)
        {
            if (r.Confidence <= 0.1)
            {
                continue;
            }

            (double x, double y, double w, double h) = r.Quad is { Length: 4 } q
                ? Bounds(q, s)
                : (r.Box.X1 * s, r.Box.Y1 * s, r.Box.Width * s, r.Box.Height * s);

            // OCRia reports no angle (as LiteParse's ocria engine), so the quad's point order gives the rotation.
            double rotation = r.Rotation != 0
                ? CanonicalQuarter(r.Rotation)
                : r.Quad is { Length: 4 } p ? PolygonRotationDeg(p) : 0;

            if (hasNativeText && IsRuleArtifact(r.Text, w, h))
            {
                continue;
            }

            List<int> inside = [];
            bool overlaps;
            if (partial)
            {
                double covered = NativeWidthCoverage(kept, x, y, w, h);
                if (covered < PartialNativeCoverage)
                {
                    for (int i = 0; i < kept.Count; i++)
                    {
                        TextItem it = kept[i];
                        double cx = it.X + (it.Width / 2), cy = it.Y + (it.Height / 2);
                        if (cx >= x && cx <= x + w && cy >= y && cy <= y + h)
                        {
                            inside.Add(i);
                        }
                    }
                }

                overlaps = covered >= PartialNativeCoverage;
            }
            else
            {
                overlaps = OverlapsExistingText(kept, x, y, w, h, OverlapTolerance);
            }

            if (overlaps)
            {
                continue;
            }

            string cleaned = CleanOcrTableArtifacts(r.Text);
            if (cleaned.Length == 0)
            {
                continue;
            }

            double? polyH = r.Quad is { Length: 4 } pq
                ? Math.Sqrt(Math.Pow(pq[3].X - pq[0].X, 2) + Math.Pow(pq[3].Y - pq[0].Y, 2)) * s
                : null;
            double fontSize = rotation is 90 or 270
                ? Math.Max(w, 1.0)
                : polyH is > 0 ? Math.Min(polyH.Value, h) : h;

            TextItem item = new()
            {
                Text = cleaned,
                X = x,
                Y = y,
                Width = w,
                Height = h,
                Rotation = rotation,
                FontName = "OCR",
                FontSize = fontSize,
                Confidence = Math.Round(r.Confidence * 1000.0, MidpointRounding.AwayFromZero) / 1000.0,
            };
            ocr.Add(new AcceptedOcrLine(r with { Text = cleaned }, item));
            foreach (int i in inside)
            {
                superseded[i] = true;
            }
        }

        List<TextItem> finalKept = [];
        for (int i = 0; i < kept.Count; i++)
        {
            (superseded[i] ? dropped : finalKept).Add(kept[i]);
        }

        return new OcrMergeResult(finalKept, ocr, dropped);
    }

    private static (double X, double Y, double W, double H) Bounds(PointF[] q, double s)
    {
        BoxF b = BoxF.Bounds(q);
        return (b.X1 * s, b.Y1 * s, (b.X2 - b.X1) * s, (b.Y2 - b.Y1) * s);
    }

    /// <summary>0, 90, 180 or 270 for a rotation in degrees.</summary>
    public static int CanonicalQuarter(double rot)
    {
        double m = ((rot % 360.0) + 360.0) % 360.0;
        return ((int)Math.Round(m / 90.0, MidpointRounding.AwayFromZero) % 4) * 90;
    }

    /// <summary>A discrete CCW rotation from a 4-point polygon, reading along its longer edge; 0 for near-square or degenerate ones.</summary>
    public static double PolygonRotationDeg(IReadOnlyList<PointF> poly)
    {
        double e0x = poly[1].X - poly[0].X, e0y = poly[1].Y - poly[0].Y;
        double e1x = poly[2].X - poly[1].X, e1y = poly[2].Y - poly[1].Y;
        double len0 = Math.Sqrt((e0x * e0x) + (e0y * e0y)), len1 = Math.Sqrt((e1x * e1x) + (e1y * e1y));
        if (Math.Max(len0, len1) < 1.0)
        {
            return 0;
        }

        (double longer, double shorter) = len0 >= len1 ? (len0, len1) : (len1, len0);
        if (shorter > 0 && longer / shorter < 1.3)
        {
            return 0;
        }

        (double rx, double ry) = len0 >= len1 ? (e0x, e0y) : (e1x, e1y);
        return CanonicalQuarter(-Math.Atan2(ry, rx) * 180.0 / Math.PI);
    }

    /// <summary>A misread rule or decoration: no letters or digits, or a single bar-like glyph far narrower than tall.</summary>
    public static bool IsRuleArtifact(string text, double w, double h)
    {
        string t = text.Trim();
        if (t.Length == 0 || !t.EnumerateRunes().Any(r => Garble.IsAlphabetic(r) || System.Text.Rune.IsNumber(r)))
        {
            return true;
        }

        return t.Length == 1 && "|Il1!ij[](){}/\\".Contains(t[0], StringComparison.Ordinal) && h > 0 && w < RuleArtifactMaxAspect * h;
    }

    /// <summary>Share of the box's width covered by items on its line (vertical overlap over half the shorter height), extents merged.</summary>
    public static double NativeWidthCoverage(IReadOnlyList<TextItem> items, double x, double y, double w, double h)
    {
        if (w <= 0)
        {
            return 1.0;
        }

        List<(double A, double B)> spans = [.. items
            .Where(it => Math.Min(y + h, it.Y + it.Height) - Math.Max(y, it.Y) > 0.5 * Math.Min(h, it.Height))
            .Select(it => (Math.Max(it.X, x), Math.Min(it.X + it.Width, x + w)))
            .Where(p => p.Item2 > p.Item1)
            .OrderBy(p => p.Item1)];
        double covered = 0, end = double.NegativeInfinity;
        foreach ((double a, double b) in spans)
        {
            if (b > end)
            {
                covered += b - Math.Max(a, end);
                end = b;
            }
        }

        return covered / w;
    }

    /// <summary>True when the box overlaps any item, each widened by <paramref name="tolerance"/>.</summary>
    public static bool OverlapsExistingText(IReadOnlyList<TextItem> items, double x, double y, double w, double h, double tolerance)
    {
        foreach (TextItem it in items)
        {
            bool ox = x < it.X + it.Width + tolerance && x + w > it.X - tolerance;
            bool oy = y < it.Y + it.Height + tolerance && y + h > it.Y - tolerance;
            if (ox && oy)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Strips table-border misreads (<c>| [ ] ( ) { }</c>) around numeric-looking content, keeping accounting negatives.</summary>
    public static string CleanOcrTableArtifacts(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        char[] border = ['|', '[', ']', '(', ')', '{', '}'];
        string core = trimmed.TrimStart(border).TrimEnd(border).Trim();
        if (core.Length == 0)
        {
            return trimmed;
        }

        bool numericIsh = core.All(c => char.IsAsciiDigit(c) || c is ',' or '.' or ' ' or '%' or '-' or '+' or '*' or '/')
            || core is "N/A" or "Z" or "-";
        if (!numericIsh)
        {
            return trimmed;
        }

        int at = trimmed.IndexOf(core, StringComparison.Ordinal);
        if (at >= 0 && trimmed[..at].EndsWith('(') && trimmed[(at + core.Length)..].StartsWith(')'))
        {
            return $"({core})";
        }

        return core;
    }

    /// <summary>Filled path area not covered by text items (per-item intersections, clamped to each path's area).</summary>
    public static double UncoveredPathArea(IReadOnlyList<RectD> paths, IReadOnlyList<TextItem> items)
    {
        double uncovered = 0;
        foreach (RectD p in paths)
        {
            double area = p.Width * p.Height;
            if (area <= 0)
            {
                continue;
            }

            double covered = 0;
            foreach (TextItem it in items)
            {
                double ix = Math.Min(p.X + p.Width, it.X + it.Width) - Math.Max(p.X, it.X);
                double iy = Math.Min(p.Y + p.Height, it.Y + it.Height) - Math.Max(p.Y, it.Y);
                if (ix > 0 && iy > 0)
                {
                    covered += ix * iy;
                    if (covered >= area)
                    {
                        break;
                    }
                }
            }

            uncovered += Math.Max(0, area - covered);
        }

        return uncovered;
    }
}
