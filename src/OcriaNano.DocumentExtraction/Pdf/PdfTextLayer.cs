// Page facts for the OCR gate, following LiteParse crates/liteparse/src/ocr_merge.rs (text items, image bounds,
// filled path bounds, invisible text share), Apache-2.0.
using System.Text;
using OcriaNano.DocumentExtraction.Layout;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace OcriaNano.DocumentExtraction.Pdf;

/// <summary>A text item on a page (native run or merged OCR line), in points with a top-left origin.</summary>
internal sealed class TextItem
{
    public string Text { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>The PDF font name (with its subset tag), "OCR" for OCR lines, or null when unknown.</summary>
    public string? FontName { get; set; }

    /// <summary>True when the text's Unicode mapping failed. PdfPig doesn't report this, so native items leave it false.</summary>
    public bool UnicodeMapError { get; set; }

    /// <summary>Counter-clockwise rotation in degrees (0, 90, 180 or 270).</summary>
    public double Rotation { get; set; }

    public double? FontSize { get; set; }

    /// <summary>The OCR confidence (rounded to 3 decimals), or null for native text.</summary>
    public double? Confidence { get; set; }

    public static TextItem Of(string text, double x, double y, double w, double h, string? font = null)
        => new() { Text = text, X = x, Y = y, Width = w, Height = h, FontName = font };

    public override string ToString() => $"{Text} @({X:0.#},{Y:0.#},{Width:0.#},{Height:0.#})";
}

/// <summary>An axis-aligned rectangle in points with a top-left origin.</summary>
internal readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>What the OCR gate needs to know about a PDF page.</summary>
internal sealed class PdfPageFacts
{
    public double Width { get; init; }

    public double Height { get; init; }

    public List<TextItem> Items { get; init; } = [];

    /// <summary>Raster images at least <see cref="OcrPageSelector.MinImageSizePt"/> on both sides, full-page ones included.</summary>
    public List<RectD> Images { get; init; } = [];

    /// <summary>Dark filled vector paths (glyph-outline candidates) between 3pt and 90% of the page.</summary>
    public List<RectD> FilledPaths { get; init; } = [];

    /// <summary>Share of the page's visible characters drawn with text render mode 3 (invisible).</summary>
    public double InvisibleShare { get; init; }
}

/// <summary>Reads a page's native text items, images and filled paths with PdfPig.</summary>
internal static class PdfTextLayer
{
    private const double MinPathSizePt = 3.0;
    private const double MaxPathPageCoverage = 0.9;

    public static PdfPageFacts Read(Page page)
    {
        Frame f = Frame.Of(page);
        return new PdfPageFacts
        {
            Width = f.W,
            Height = f.H,
            Items = Items(page, f),
            Images = Images(page, f),
            FilledPaths = FilledPaths(page, f),
            InvisibleShare = InvisibleShare(page),
        };
    }

    /// <summary>Native text items: PdfPig words joined into runs of one font on one line.</summary>
    internal static List<TextItem> Items(Page page, Frame f)
    {
        List<TextItem> items = [];
        TextItem? cur = null;
        StringBuilder sb = new();
        foreach (Word word in page.GetWords())
        {
            string text = word.Text ?? "";
            if (text.Trim().Length == 0)
            {
                continue;
            }

            RectD r = f.ToPage(word.BoundingBox);
            string? font = word.Letters.Count > 0 ? word.Letters[0].FontName : word.FontName;
            if (cur is not null && cur.FontName == font && SameRun(cur, r))
            {
                sb.Append(' ').Append(text);
                double x0 = Math.Min(cur.X, r.X), y0 = Math.Min(cur.Y, r.Y);
                double x1 = Math.Max(cur.X + cur.Width, r.X + r.Width), y1 = Math.Max(cur.Y + cur.Height, r.Y + r.Height);
                (cur.X, cur.Y, cur.Width, cur.Height) = (x0, y0, x1 - x0, y1 - y0);
                continue;
            }

            Flush();
            cur = new TextItem { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, FontName = font };
            sb.Append(text);
        }

        Flush();
        return items;

        void Flush()
        {
            if (cur is not null)
            {
                cur.Text = sb.ToString();
                items.Add(cur);
            }

            cur = null;
            sb.Clear();
        }
    }

    private static bool SameRun(TextItem cur, RectD r)
    {
        double h = Math.Max(cur.Height, r.Height);
        double cy0 = cur.Y + (cur.Height / 2), cy1 = r.Y + (r.Height / 2);
        double gap = r.X - (cur.X + cur.Width);
        return Math.Abs(cy0 - cy1) < 0.5 * h && gap > -0.5 * h && gap < 1.5 * h;
    }

    private static List<RectD> Images(Page page, Frame f)
    {
        List<RectD> images = [];
        foreach (IPdfImage image in page.GetImages())
        {
            RectD r = f.ToPage(image.BoundingBox);
            if (r.Width >= OcrPageSelector.MinImageSizePt && r.Height >= OcrPageSelector.MinImageSizePt)
            {
                images.Add(r);
            }
        }

        return images;
    }

    private static List<RectD> FilledPaths(Page page, Frame f)
    {
        List<RectD> paths = [];
        foreach (PdfPath path in page.Paths)
        {
            if (!path.IsFilled || path.IsClipping)
            {
                continue;
            }

            // glyph outlines are ink-dark; light fills are shading and zebra striping
            if (path.FillColor is { } color)
            {
                (double r, double g, double b) = color.ToRGBValues();
                if ((0.299 * r * 255) + (0.587 * g * 255) + (0.114 * b * 255) > 140)
                {
                    continue;
                }
            }

            if (path.GetBoundingRectangle() is not PdfRectangle bounds)
            {
                continue;
            }

            RectD rect = f.ToPage(bounds);
            if (rect.Width < MinPathSizePt || rect.Height < MinPathSizePt)
            {
                continue;
            }

            if (rect.Width > f.W * MaxPathPageCoverage && rect.Height > f.H * MaxPathPageCoverage)
            {
                continue;
            }

            paths.Add(rect);
        }

        return paths;
    }

    private static double InvisibleShare(Page page)
    {
        int invisible = 0, total = 0;
        foreach (Letter l in page.Letters)
        {
            string v = l.Value ?? "";
            if (v.Length == 0 || char.IsWhiteSpace(v[0]) || char.IsControl(v[0]))
            {
                continue;
            }

            total++;
            if ((int)l.RenderingMode == 3)
            {
                invisible++;
            }
        }

        return total == 0 ? 0 : (double)invisible / total;
    }

    /// <summary>Maps PDF user space to the rendered page: crop box offset, y flipped, <c>/Rotate</c> applied.</summary>
    internal readonly record struct Frame(double Left, double Top, double W0, double H0, int Rot, double W, double H)
    {
        public static Frame Of(Page page)
        {
            PdfRectangle crop = page.CropBox.Bounds;
            double w0 = crop.Right - crop.Left, h0 = crop.Top - crop.Bottom;
            int rot = (((page.Rotation.Value / 90) % 4) + 4) % 4;
            (double w, double h) = rot % 2 == 1 ? (h0, w0) : (w0, h0);
            return new Frame(crop.Left, crop.Top, w0, h0, rot, w, h);
        }

        public RectD ToPage(PdfRectangle r)
        {
            double x0 = Math.Min(r.Left, r.Right), x1 = Math.Max(r.Left, r.Right);
            double y0 = Math.Min(r.Bottom, r.Top), y1 = Math.Max(r.Bottom, r.Top);
            Box4 b = new(x0 - Left, Top - y1, x1 - Left, Top - y0);
            b = Rot switch
            {
                1 => new Box4(H0 - b.Y1, b.X0, H0 - b.Y0, b.X1),
                2 => new Box4(W0 - b.X1, H0 - b.Y1, W0 - b.X0, H0 - b.Y0),
                3 => new Box4(b.Y0, W0 - b.X1, b.Y1, W0 - b.X0),
                _ => b,
            };
            return new RectD(b.X0, b.Y0, b.X1 - b.X0, b.Y1 - b.Y0);
        }
    }
}
