using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Layout;
using SkiaSharp;

namespace OcriaNano.DocumentExtraction.Pdf;

/// <summary>Renders PDF pages with PDFium (through PDFtoImage). PDFium isn't thread-safe, so calls are serialized.</summary>
internal static class PageRenderer
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Renders a page for OCR at <paramref name="dpi"/>, lowered so the long edge stays within 4096 px. Returns the RGB
    /// image and the points-per-pixel scale of the bitmap actually produced.
    /// </summary>
    public static (RgbImage Image, double PointsPerPixel) RenderForOcr(byte[] pdf, int pageIndex, double pageWidthPt, double pageHeightPt, double dpi)
    {
        double eff = OcrPageSelector.RenderDpi(pageWidthPt, pageHeightPt, dpi);
        Bgra32Image bgra = Render(pdf, pageIndex, Math.Max(1, (int)Math.Floor(eff)));
        double ppp = bgra.Width > 0 ? pageWidthPt / bgra.Width : 72.0 / eff;
        return (bgra.ToRgb(), ppp);
    }

    /// <summary>Renders a page at 150 DPI for LayoutNet's rule features.</summary>
    public static Bgra32Image RenderForLayout(byte[] pdf, int pageIndex) => Render(pdf, pageIndex, Units.RulesDpi);

    /// <summary>Renders a page as a bitmap the caller owns.</summary>
    public static SKBitmap RenderBitmap(byte[] pdf, int pageIndex, int dpi)
    {
        lock (Gate)
        {
            return PDFtoImage.Conversion.ToImage(pdf, pageIndex, password: null, options: new(Dpi: dpi));
        }
    }

    private static Bgra32Image Render(byte[] pdf, int pageIndex, int dpi)
    {
        lock (Gate)
        {
            using SKBitmap bmp = PDFtoImage.Conversion.ToImage(pdf, pageIndex, password: null, options: new(Dpi: dpi));
            return ToBgra(bmp);
        }
    }

    /// <summary>Copies a bitmap into a tightly packed BGRA buffer.</summary>
    public static Bgra32Image ToBgra(SKBitmap bmp)
    {
        using SKBitmap? converted = bmp.ColorType == SKColorType.Bgra8888 ? null : bmp.Copy(SKColorType.Bgra8888);
        SKBitmap src = converted ?? bmp;
        int w = src.Width, h = src.Height;
        byte[] data = new byte[w * h * 4];
        if (src.RowBytes == w * 4)
        {
            src.GetPixelSpan().CopyTo(data);
        }
        else
        {
            ReadOnlySpan<byte> span = src.GetPixelSpan();
            for (int y = 0; y < h; y++)
            {
                span.Slice(y * src.RowBytes, w * 4).CopyTo(data.AsSpan(y * w * 4, w * 4));
            }
        }

        return new Bgra32Image(w, h, data);
    }
}
