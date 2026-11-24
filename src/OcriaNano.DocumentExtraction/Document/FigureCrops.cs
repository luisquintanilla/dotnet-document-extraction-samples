using OcriaNano.DocumentExtraction.Layout;
using OcriaNano.DocumentExtraction.Pdf;
using SkiaSharp;

namespace OcriaNano.DocumentExtraction.Document;

/// <summary>PNG crops of the figures LayoutNet found, for <see cref="OcriaNanoOptions.IncludeFigureImages"/>.</summary>
internal static class FigureCrops
{
    /// <summary>Margin added around a figure, in points.</summary>
    public const double PaddingPt = 4;

    /// <summary>An embedded image covering more than this share of the page is a scan or a background, not a figure.</summary>
    public const double MaxImagePageShare = 0.5;

    /// <summary>An embedded image this close to a figure's text (in points) belongs to the figure, e.g. a caption under a thumbnail.</summary>
    public const double ImageReachPt = 12;

    /// <summary>
    /// Crops each FigureText block from the page raster. A block's region is its box (turned back onto the page when the
    /// page was deskewed) joined with every embedded image it overlaps or nearly touches, so a figure's crop covers the graphic and not
    /// just its labels.
    /// </summary>
    /// <param name="page">The page, with its model, size and deskew.</param>
    /// <param name="images">Embedded image rectangles in page points (empty for image inputs and scans).</param>
    /// <param name="bmp">The whole page rendered at any resolution; the caller owns it.</param>
    public static IReadOnlyDictionary<ModelBlock, byte[]> Crop(PageResult page, IReadOnlyList<RectD> images, SKBitmap bmp)
    {
        Dictionary<ModelBlock, byte[]> crops = new(ReferenceEqualityComparer.Instance);
        if (page.Width <= 0 || page.Height <= 0)
        {
            return crops;
        }

        double k = bmp.Width / page.Width;
        foreach (ModelBlock fig in Figures(page.Model))
        {
            Box4 r = Region(page, fig.BBox, images);
            SKRectI px = SKRectI.Create(
                Math.Max(0, (int)Math.Floor((r.X0 - PaddingPt) * k)),
                Math.Max(0, (int)Math.Floor((r.Y0 - PaddingPt) * k)),
                0,
                0);
            px.Right = Math.Min(bmp.Width, (int)Math.Ceiling((r.X1 + PaddingPt) * k));
            px.Bottom = Math.Min(bmp.Height, (int)Math.Ceiling((r.Y1 + PaddingPt) * k));
            if (px.Width < 2 || px.Height < 2)
            {
                continue;
            }

            using SKBitmap sub = new();
            if (!bmp.ExtractSubset(sub, px))
            {
                continue;
            }

            using SKImage img = SKImage.FromBitmap(sub);
            using SKData png = img.Encode(SKEncodedImageFormat.Png, 100);
            crops[fig] = png.ToArray();
        }

        return crops;
    }

    /// <summary>The FigureText blocks worth an image element.</summary>
    public static IEnumerable<ModelBlock> Figures(PageModel model)
        => model.Blocks.Where(b => b.Class == LayoutClass.FigureText && !string.IsNullOrWhiteSpace(b.Text));

    internal static Box4 Region(PageResult page, Box4 box, IReadOnlyList<RectD> images)
    {
        if (page.Deskew is double deg)
        {
            IReadOnlyList<(double X, double Y)> pts = Ocr.Deskew.RotateBack(UnitSource.Rect(box).Polygon, page.DeskewCenter.X, page.DeskewCenter.Y, deg);
            box = new Box4(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }

        double pageArea = page.Width * page.Height, r = ImageReachPt;
        Box4 text = box;
        foreach (RectD im in images)
        {
            bool near = im.X < text.X1 + r && im.X + im.Width > text.X0 - r && im.Y < text.Y1 + r && im.Y + im.Height > text.Y0 - r;
            if (near && im.Area <= MaxImagePageShare * pageArea)
            {
                box = new Box4(Math.Min(box.X0, im.X), Math.Min(box.Y0, im.Y), Math.Max(box.X1, im.X + im.Width), Math.Max(box.Y1, im.Y + im.Height));
            }
        }

        return box;
    }
}
