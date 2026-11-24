// Ported from LiteParse crates/liteparse/src/ocr_merge.rs (calculate_page_complexity, ComplexityReason, fitting_dpi,
// uncovered_path_area, native_text_length, render_pages_for_ocr_capped's has_native_text / replace_native / mask_native), Apache-2.0.
using System.Text;

namespace OcriaNano.DocumentExtraction.Pdf;

/// <summary>Why a PDF page was sent to OCR.</summary>
internal enum OcrReason
{
    /// <summary>A full-page raster with (almost) no native text.</summary>
    Scanned,

    /// <summary>(Almost) no native text and no full-page raster.</summary>
    NoText,

    /// <summary>Real text, but too thin to be the whole page.</summary>
    SparseText,

    /// <summary>Embedded raster figures sit alongside the native text.</summary>
    EmbeddedImages,

    /// <summary>The native text decodes to garbage.</summary>
    Garbled,

    /// <summary>Text painted as filled vector outlines.</summary>
    VectorText,

    /// <summary>A scan whose native text is an invisible earlier-OCR layer.</summary>
    InvisibleTextLayer,

    /// <summary>OCR was forced by <see cref="OcrMode.Always"/>.</summary>
    Forced,
}

/// <summary>The OCR gate's verdict on one page.</summary>
internal sealed record PageComplexity(
    int TextLength,
    double TextCoverage,
    int ImageCount,
    bool FullPageImage,
    double? UncoveredVectorArea,
    bool IsGarbled,
    IReadOnlyList<OcrReason> Reasons)
{
    public bool NeedsOcr => Reasons.Count > 0;

    /// <summary>
    /// Whether <see cref="OcrMode.Auto"/> OCRs the page. Unlike LiteParse, a page with a healthy text layer isn't OCR'd
    /// just for an embedded figure or for being short: only when the text layer is unusable (scanned, blank, garbled,
    /// invisible), text is drawn as vector outlines, or the text is sparse <em>and</em> an image may carry the content.
    /// </summary>
    public bool RunsOcr => Reasons.Any(r => r is not (OcrReason.EmbeddedImages or OcrReason.SparseText))
        || (Reasons.Contains(OcrReason.EmbeddedImages) && Reasons.Contains(OcrReason.SparseText));

    /// <summary>True unless the page went to OCR as scanned, blank, garbled or an invisible layer (drives the merge's artifact filters).</summary>
    public bool HasNativeText => !Reasons.Any(r => r is OcrReason.Scanned or OcrReason.NoText or OcrReason.Garbled or OcrReason.InvisibleTextLayer or OcrReason.Forced);

    /// <summary>True when OCR replaces every native item.</summary>
    public bool ReplaceNative => Reasons.Any(r => r is OcrReason.InvisibleTextLayer or OcrReason.Forced);
}

/// <summary>Decides which PDF pages need OCR (port of <c>calculate_page_complexity</c>).</summary>
internal static class OcrPageSelector
{
    public const double MinImageSizePt = 25.0;
    public const double MaxImagePageCoverage = 0.9;
    public const double UncoveredVectorAreaThreshold = 400.0;
    public const int SparseTextTrustedLen = 500;
    public const int MinNativeTextLen = 20;
    public const double InvisibleLayerMinShare = 0.9;
    public const int MaxOcrRenderLongEdgePx = 4096;

    /// <summary>Names as LiteParse reports them.</summary>
    public static string AsString(this OcrReason reason) => reason switch
    {
        OcrReason.Scanned => "scanned",
        OcrReason.NoText => "no-text",
        OcrReason.SparseText => "sparse-text",
        OcrReason.EmbeddedImages => "embedded-images",
        OcrReason.Garbled => "garbled",
        OcrReason.VectorText => "vector-text",
        OcrReason.InvisibleTextLayer => "invisible-text-layer",
        _ => "forced",
    };

    /// <summary>The gate for a page whose OCR mode is <see cref="OcrMode.Auto"/>.</summary>
    public static PageComplexity Evaluate(PdfPageFacts page)
    {
        double pw = page.Width, ph = page.Height;
        int textLength = NativeTextLength(page.Items);
        bool IsFullPage(RectD b) => b.Width > pw * MaxImagePageCoverage && b.Height > ph * MaxImagePageCoverage;
        List<RectD> all = [.. page.Images.Where(b => b.Width >= MinImageSizePt && b.Height >= MinImageSizePt)];
        bool topLevelScan = all.Any(IsFullPage);
        int counted = all.Count(b => !IsFullPage(b));
        double pageArea = pw * ph;
        double textArea = page.Items.Where(i => !Garble.IsUnusableNative(i)).Sum(i => i.Width * i.Height);
        double coverage = pageArea > 0 ? textArea / pageArea : 0;
        bool sparse = textLength < 2000 && coverage < 0.15;
        bool garbled = Garble.PageIsGarbled(page.Items);

        List<OcrReason> reasons = [];
        if (textLength < MinNativeTextLen)
        {
            reasons.Add(topLevelScan ? OcrReason.Scanned : OcrReason.NoText);
        }
        else if (sparse && (topLevelScan || textLength < SparseTextTrustedLen))
        {
            reasons.Add(OcrReason.SparseText);
        }

        if (counted > 0)
        {
            reasons.Add(OcrReason.EmbeddedImages);
        }

        if (garbled)
        {
            reasons.Add(OcrReason.Garbled);
        }

        if (topLevelScan && textLength >= MinNativeTextLen && page.InvisibleShare >= InvisibleLayerMinShare)
        {
            reasons.Add(OcrReason.InvisibleTextLayer);
        }

        double? uncovered = null;
        if (reasons.Count == 0)
        {
            uncovered = OcrMerge.UncoveredPathArea(page.FilledPaths, page.Items);
            if (uncovered >= UncoveredVectorAreaThreshold)
            {
                reasons.Add(OcrReason.VectorText);
            }
        }

        return new PageComplexity(textLength, coverage, counted, topLevelScan, uncovered, garbled, reasons);
    }

    /// <summary>The verdict for a page OCR'd because of <see cref="OcrMode.Always"/>: the new OCR replaces the text layer.</summary>
    public static PageComplexity Forced(PdfPageFacts page)
        => new(NativeTextLength(page.Items), 0, 0, false, null, false, [OcrReason.Forced]);

    /// <summary>UTF-8 bytes of usable native text.</summary>
    public static int NativeTextLength(IEnumerable<TextItem> items)
        => items.Where(i => !Garble.IsUnusableNative(i)).Sum(i => Encoding.UTF8.GetByteCount(i.Text));

    /// <summary>True when the merge keeps the native text as is, so it can be blanked out of the OCR raster.</summary>
    public static bool MaskNative(PageComplexity c, IReadOnlyList<TextItem> items)
        => c.HasNativeText && !c.ReplaceNative && Garble.Scope(items).Kind == GarbledKind.None;

    /// <summary><paramref name="dpi"/>, lowered so the long edge is at most <paramref name="maxLongEdge"/> pixels (0 = no cap).</summary>
    public static double FittingDpi(double width, double height, double dpi, int maxLongEdge)
    {
        double longEdge = Math.Max(width, height);
        return maxLongEdge == 0 || longEdge <= 0 ? dpi : Math.Min(dpi, maxLongEdge * 72.0 / longEdge);
    }

    /// <summary>The OCR render DPI for a page: <paramref name="dpi"/> clamped so the long edge stays within 4096 px.</summary>
    public static double RenderDpi(double width, double height, double dpi) => FittingDpi(width, height, dpi, MaxOcrRenderLongEdgePx);
}
