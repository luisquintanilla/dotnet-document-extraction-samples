using System.Globalization;
using Microsoft.Extensions.Documents;
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Layout;
using OcriaNano.DocumentExtraction.Ocr;
using OcriaNano.DocumentExtraction.Onnx;
using OcriaNano.DocumentExtraction.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OcriaNano.DocumentExtraction.Document;

/// <summary>Per-call settings: the client's options with <c>ocria.*</c> overrides applied.</summary>
internal sealed record ExtractionSettings(OcrMode Ocr, int Dpi, bool Layout, string? Pages, int MaxDegreeOfParallelism = 1, bool FigureImages = false);

/// <summary>Runs one document through the OCR gate, OCR, merge, LayoutNet and the builder.</summary>
internal sealed class ExtractionPipeline(SessionPool sessions, OcriaEngine engine)
{
    /// <summary>Images are treated as a page whose long side is this many points (US Letter height).</summary>
    public const double ImageLongSidePt = 792;

    /// <summary>Layout raster long side for images: <see cref="ImageLongSidePt"/> at 150 DPI.</summary>
    public const int ImageLayoutLongSidePx = 1650;

    public ExtractionDocument ExtractPdf(byte[] bytes, string identifier, ExtractionSettings settings, CancellationToken ct)
    {
        using PdfDocument pdf = PdfDocument.Open(bytes);
        IReadOnlyList<int> numbers = PageSelection.Parse(settings.Pages, pdf.NumberOfPages);
        PageResult[] pages = new PageResult[numbers.Count];
        Lock pdfGate = new();
        int dop = Math.Clamp(settings.MaxDegreeOfParallelism, 1, Math.Max(1, numbers.Count));
        if (dop == 1)
        {
            for (int i = 0; i < numbers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                pages[i] = PdfPage(bytes, pdf, numbers[i], settings, pdfGate, ct);
            }
        }
        else
        {
            // PdfPig isn't thread-safe, so page reads share a lock; rendering is serialized in PageRenderer and the
            // ONNX sessions are thread-safe, so OCR and LayoutNet run in parallel.
            try
            {
                Parallel.For(
                    0,
                    numbers.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                    i => pages[i] = PdfPage(bytes, pdf, numbers[i], settings, pdfGate, ct));
            }
            catch (AggregateException e) when (e.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
            }
        }

        return ExtractionDocumentBuilder.Build(identifier, pages);
    }

    public ExtractionDocument ExtractImage(byte[] bytes, string identifier, ExtractionSettings settings, CancellationToken ct)
    {
        using SKBitmap bmp = SKBitmap.Decode(bytes) ?? throw new InvalidDataException($"'{identifier}' isn't a readable image.");
        Bgra32Image bgra = PageRenderer.ToBgra(bmp);
        double s = ImageLongSidePt / Math.Max(bmp.Width, bmp.Height);
        OcrPage ocr = engine.Recognize(bgra.ToRgb(), ct);
        List<OcrLine> lines = [.. ocr.Lines.Where(Usable)];
        (List<Unit> units, double? skew) = UprightUnits(lines, bmp.Width, bmp.Height, s);
        List<UnitSource> sources = [.. lines.Select(l => OcrSource(l, s))];
        double w = bmp.Width * s, h = bmp.Height * s;
        (PageModel model, string? skipped) = settings.Layout
            ? RunLayout(units, w, h, ocr: true, () => ImageRaster(bmp), ct)
            : (PageModel.Empty, "disabled");
        PageResult page = new()
        {
            Number = 1,
            Width = w,
            Height = h,
            SourceScale = 1 / s,
            CoordinateUnit = ExtractionDocumentCoordinateUnit.Pixel,
            Units = units,
            Sources = sources,
            Model = model,
            LayoutSkipped = skipped,
            Ocred = true,
            Scripts = [.. lines.Select(l => l.Script).Distinct()],
            Deskew = skew,
            DeskewCenter = (w / 2, h / 2),
        };
        if (settings.FigureImages && FigureCrops.Figures(model).Any())
        {
            page.FigureImages = FigureCrops.Crop(page, [], bmp);
        }

        return ExtractionDocumentBuilder.Build(identifier, [page]);
    }

    private PageResult PdfPage(byte[] bytes, PdfDocument pdf, int number, ExtractionSettings settings, Lock pdfGate, CancellationToken ct)
    {
        PdfPageFacts facts;
        List<Unit> native;
        double w, h;
        using (pdfGate.EnterScope())
        {
            Page pdfPage = pdf.GetPage(number);
            facts = PdfTextLayer.Read(pdfPage);
            (native, w, h) = PdfUnits.FromPage(pdfPage);
        }

        PageComplexity? c = settings.Ocr switch
        {
            OcrMode.Never => null,
            OcrMode.Always => OcrPageSelector.Forced(facts),
            _ => OcrPageSelector.Evaluate(facts),
        };

        List<Unit> units;
        List<UnitSource> sources;
        bool ocred = c is { RunsOcr: true };
        bool unitsFromOcr = false;
        double? skew = null;
        (double X, double Y) center = default;
        List<string> scripts = [];
        if (!ocred)
        {
            units = native;
            sources = [.. native.Select(u => UnitSource.Rect(u.PBox))];
        }
        else
        {
            (RgbImage image, double ppp) = PageRenderer.RenderForOcr(bytes, number - 1, facts.Width, facts.Height, settings.Dpi);
            if (OcrPageSelector.MaskNative(c!, facts.Items))
            {
                NativeTextMask.Apply(image, facts.Width, facts.Height, facts.Items);
            }

            OcrPage page = engine.Recognize(image, ct);
            OcrMergeResult merged = OcrMerge.Merge(facts.Items, page.Lines, ppp, c!.HasNativeText, c.ReplaceNative);
            List<OcrLine> lines = [.. merged.Ocr.Select(a => a.Line with { Text = a.Item.Text }).Where(Usable)];
            scripts.AddRange(lines.Select(l => l.Script).Distinct());
            List<UnitSource> ocrSources = [.. lines.Select(l => OcrSource(l, ppp))];
            if (merged.KeptNatives.Count == 0)
            {
                (units, skew) = UprightUnits(lines, image.Width, image.Height, ppp);
                center = (image.Width * ppp / 2, image.Height * ppp / 2);
                sources = ocrSources;
                unitsFromOcr = true;
            }
            else
            {
                List<Unit> kept = [.. native.Where(u => !merged.DroppedNatives.Any(d => Inside(u.PBox, d)))];
                units = [.. kept, .. OcrUnits.FromOcr(lines, ppp)];
                sources = [.. kept.Select(u => UnitSource.Rect(u.PBox)), .. ocrSources];
            }
        }

        (PageModel model, string? skipped) = settings.Layout
            ? RunLayout(units, w, h, unitsFromOcr, () => PageRenderer.RenderForLayout(bytes, number - 1), ct)
            : (PageModel.Empty, "disabled");
        PageResult result = new()
        {
            Number = number,
            Width = w,
            Height = h,
            Units = units,
            Sources = sources,
            Model = model,
            LayoutSkipped = skipped,
            Ocred = ocred,
            Scripts = scripts,
            Deskew = skew,
            DeskewCenter = center,
        };
        if (settings.FigureImages && FigureCrops.Figures(model).Any())
        {
            using SKBitmap bmp = PageRenderer.RenderBitmap(bytes, number - 1, Units.RulesDpi);
            result.FigureImages = FigureCrops.Crop(result, facts.Images, bmp);
        }

        return result;
    }

    /// <summary>
    /// Units for a page whose text is all OCR. On a tilted scan the lines are turned upright about the image centre
    /// first (see <see cref="Deskew"/>); sources keep the lines' real quads.
    /// </summary>
    private static (List<Unit> Units, double? Skew) UprightUnits(List<OcrLine> lines, int widthPx, int heightPx, double pointsPerPixel)
    {
        if (Deskew.PageSkew(lines) is not double deg)
        {
            return (OcrUnits.FromOcr(lines, pointsPerPixel), null);
        }

        List<OcrLine> upright = [.. lines.Select(l => Deskew.Upright(l, widthPx / 2.0, heightPx / 2.0, deg))];
        return (OcrUnits.FromOcr(upright, pointsPerPixel), deg);
    }

    private (PageModel Model, string? Skipped) RunLayout(List<Unit> units, double w, double h, bool ocr, Func<Bgra32Image> raster, CancellationToken ct)
    {
        if (!sessions.Exists(OcriaNanoModels.TableNet))
        {
            return (PageModel.Empty, $"{OcriaNanoModels.TableNet} not installed");
        }

        int chars = units.Sum(u => Units.CharCount(u.Text));
        if (units.Count < 2 || chars < 20)
        {
            return (PageModel.Empty, "too little text");
        }

        if (units.Count > LayoutNetRunner.MaxUnits)
        {
            return (PageModel.Empty, $"over {LayoutNetRunner.MaxUnits} units");
        }

        LayoutNetRunner runner = new(sessions.Get(OcriaNanoModels.TableNet));
        return (runner.Run(new LayoutPageInput(units, w, h, ocr, raster()), ct), null);
    }

    private static Bgra32Image ImageRaster(SKBitmap bmp)
    {
        double k = (double)ImageLayoutLongSidePx / Math.Max(bmp.Width, bmp.Height);
        int w = Math.Max(1, (int)Math.Round(bmp.Width * k)), h = Math.Max(1, (int)Math.Round(bmp.Height * k));
        using SKBitmap resized = bmp.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear)) ?? bmp.Copy(SKColorType.Bgra8888);
        return PageRenderer.ToBgra(resized);
    }

    private static bool Usable(OcrLine l) => l.Text.Trim().Length > 0 && l.Box.Width > 0 && l.Box.Height > 0;

    private static UnitSource OcrSource(OcrLine l, double scale)
    {
        if (l.Quad is { Length: 4 } q)
        {
            return new UnitSource([.. q.Select(p => (p.X * scale, p.Y * scale))], l.Confidence);
        }

        return UnitSource.Rect(new Box4(l.Box.X1 * scale, l.Box.Y1 * scale, l.Box.X2 * scale, l.Box.Y2 * scale), l.Confidence);
    }

    private static bool Inside(Box4 b, TextItem d)
    {
        double cx = (b.X0 + b.X1) / 2, cy = (b.Y0 + b.Y1) / 2;
        return cx >= d.X - 1 && cx <= d.X + d.Width + 1 && cy >= d.Y - 1 && cy <= d.Y + d.Height + 1;
    }
}

/// <summary>Parses <c>ocria.pages</c> selections such as <c>"1-3,5"</c>.</summary>
internal static class PageSelection
{
    public static IReadOnlyList<int> Parse(string? spec, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return [.. Enumerable.Range(1, pageCount)];
        }

        SortedSet<int> pages = [];
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2
                || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int a)
                || !int.TryParse(ends[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int b)
                || a < 1 || b < a)
            {
                throw new ArgumentException($"'{spec}' isn't a page selection like \"1-3,5\".", nameof(spec));
            }

            for (int p = a; p <= Math.Min(b, pageCount); p++)
            {
                pages.Add(p);
            }
        }

        return [.. pages];
    }
}
