// Contract for the LayoutNet port (LiteParse crates/liteparse/src/table_model/{mod,decode,features,units}.rs), Apache-2.0.
using Microsoft.ML.OnnxRuntime;
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Ocr;
using UglyToad.PdfPig.Content;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>What LayoutNet needs for one page.</summary>
/// <param name="Units">The page's units (native text, OCR text, or both), page frame in points.</param>
/// <param name="Width">Page width in points, as rendered.</param>
/// <param name="Height">Page height in points, as rendered.</param>
/// <param name="Ocr">Whether the units came from OCR (no usable text layer): skips the main-rotation turn.</param>
/// <param name="Raster">The page rendered at <see cref="Units.RulesDpi"/> (150) as B,G,R,A, for ruling lines and the image branch; null to run without them.</param>
internal sealed record LayoutPageInput(IReadOnlyList<Unit> Units, double Width, double Height, bool Ocr, Bgra32Image? Raster);

/// <summary>One decoded table cell. Rows and columns are inclusive ranges.</summary>
internal sealed record GridCell(int R0, int C0, int R1, int C1, string Text, Box4 Bx)
{
    /// <summary>Indices into <see cref="LayoutPageInput.Units"/> placed in this cell.</summary>
    public IReadOnlyList<int> Units { get; init; } = [];
}

/// <summary>A decoded table grid.</summary>
internal sealed record Grid(IReadOnlyList<GridCell> Cells, IReadOnlyList<int> HeadRows, int NRows, int NCols);

/// <summary>A table LayoutNet found: its grid, its box on the page (points, top-left) and its units.</summary>
internal sealed record ModelTable(Box4 BBox, Grid Grid, IReadOnlyList<int> Units);

/// <summary>A layout block: units grouped by the same-block head and labelled by their summed class probabilities.</summary>
internal sealed record ModelBlock(Box4 BBox, LayoutClass Class, string Text, IReadOnlyList<int> Units);

/// <summary>What LayoutNet found on a page.</summary>
internal sealed record PageModel(IReadOnlyList<ModelTable> Tables, IReadOnlyList<ModelBlock> Blocks)
{
    public static PageModel Empty { get; } = new([], []);
}

/// <summary>Builds units from a PdfPig page (port of <c>units::pdf_units</c>).</summary>
internal static partial class PdfUnits
{
    /// <summary>The page's units and its rendered size in points (width, height after <c>/Rotate</c>).</summary>
    public static (List<Unit> Units, double Width, double Height) FromPage(Page page) => throw new NotImplementedException();
}

/// <summary>Builds units from OCR lines (LiteParse's <c>ocr_units</c>, without projection signals).</summary>
internal static partial class OcrUnits
{
    /// <summary>One unit per OCR line, scaled from image pixels to points by <paramref name="pointsPerPixel"/>; <c>h</c> is the box height / 1.4.</summary>
    public static List<Unit> FromOcr(IReadOnlyList<OcrLine> lines, double pointsPerPixel) => throw new NotImplementedException();
}

/// <summary>Runs <c>tablenet.onnx</c> on a page and decodes tables and blocks (port of <c>run_page</c>).</summary>
internal sealed partial class LayoutNetRunner
{
    public LayoutNetRunner(InferenceSession tableNet)
    {
        TableNet = tableNet;
    }

    public InferenceSession TableNet { get; }

    /// <summary>Tables and blocks on the page; <see cref="PageModel.Empty"/> for pages with under 2 or over 1500 units, or under 20 characters.</summary>
    public PageModel Run(LayoutPageInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

internal static partial class Units
{
    /// <summary>Render resolution for ruling lines and the image branch (training rendered at 150 dpi).</summary>
    public const int RulesDpi = 150;
}
