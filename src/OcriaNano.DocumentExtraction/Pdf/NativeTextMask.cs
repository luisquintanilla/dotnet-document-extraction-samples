// Ported from LiteParse crates/liteparse/src/ocr_merge.rs (mask_native_text), Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;

namespace OcriaNano.DocumentExtraction.Pdf;

/// <summary>Blanks kept native text out of an OCR raster so OCR only reads what the text layer doesn't cover.</summary>
internal static class NativeTextMask
{
    private const double MarginPt = 1.0;

    /// <summary>Paints the boxes of usable, non-blank items white (widened by 1pt) in <paramref name="image"/>.</summary>
    public static void Apply(RgbImage image, double pageWidth, double pageHeight, IEnumerable<TextItem> items)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return;
        }

        int w = image.Width, h = image.Height;
        double sx = w / pageWidth, sy = h / pageHeight;
        foreach (TextItem item in items)
        {
            if (Garble.IsUnusableNative(item) || item.Text.Trim().Length == 0)
            {
                continue;
            }

            int x0 = (int)Math.Max(0, Math.Floor((item.X - MarginPt) * sx));
            int y0 = (int)Math.Max(0, Math.Floor((item.Y - MarginPt) * sy));
            int x1 = (int)Math.Min(w, Math.Ceiling((item.X + item.Width + MarginPt) * sx));
            int y1 = (int)Math.Min(h, Math.Ceiling((item.Y + item.Height + MarginPt) * sy));
            if (x0 >= x1 || y0 >= y1)
            {
                continue;
            }

            for (int y = y0; y < y1; y++)
            {
                image.Data.AsSpan(((y * w) + x0) * 3, (x1 - x0) * 3).Fill(255);
            }
        }
    }
}
