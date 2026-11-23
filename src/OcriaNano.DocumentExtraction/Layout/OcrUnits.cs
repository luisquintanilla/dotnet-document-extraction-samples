// Ported from LiteParse crates/liteparse/src/table_model/mod.rs (ocr_units, without projection signals), Apache-2.0.
using OcriaNano.DocumentExtraction.Ocr;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>Builds units from OCR lines (LiteParse's <c>ocr_units</c>, without projection signals).</summary>
internal static partial class OcrUnits
{
    /// <summary>One unit per OCR line, scaled from image pixels to points by <paramref name="pointsPerPixel"/>; <c>h</c> is the box height / 1.4.</summary>
    public static List<Unit> FromOcr(IReadOnlyList<OcrLine> lines, double pointsPerPixel)
    {
        List<Unit> units = [];
        foreach (OcrLine line in lines)
        {
            string t = line.Text.Trim();
            BoxF b = line.Box;
            if (t.Length == 0 || b.Width <= 0 || b.Height <= 0)
            {
                continue;
            }

            Box4 bx = new(b.X1 * pointsPerPixel, b.Y1 * pointsPerPixel, b.X2 * pointsPerPixel, b.Y2 * pointsPerPixel);
            units.Add(new Unit
            {
                Bx = bx,
                PBox = bx,
                Text = t,
                H = (bx.Y1 - bx.Y0) / 1.4,
                Size = 0,
                Bold = null,
                Q = 0,
                Minor = false,
                FromOcr = true,
            });
        }

        return units;
    }
}
