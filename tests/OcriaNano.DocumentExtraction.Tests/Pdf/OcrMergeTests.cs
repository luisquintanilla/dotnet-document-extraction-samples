using OcriaNano.DocumentExtraction.Ocr;
using OcriaNano.DocumentExtraction.Pdf;

namespace OcriaNano.DocumentExtraction.Tests.Pdf;

// Ported from LiteParse crates/liteparse/src/ocr_merge.rs tests, Apache-2.0.
public class OcrMergeTests
{
    private static TextItem Item(double x, double y, double w, double h) => TextItem.Of("x", x, y, w, h);

    private static PdfPageFacts Page(List<TextItem> items, params RectD[] images)
        => new() { Width = 612, Height = 792, Items = items, Images = [.. images] };

    private static List<TextItem> DenseText()
        => [.. Enumerable.Range(0, 40).Select(i => TextItem.Of("some meaningful text", 40, 20.0 * i, 500, 18))];

    [Fact]
    public void TextDensePageNeedsNoOcr()
    {
        PageComplexity c = OcrPageSelector.Evaluate(Page(DenseText()));
        Assert.False(c.NeedsOcr);
        Assert.Empty(c.Reasons);
        Assert.Equal(0.0, c.UncoveredVectorArea);
    }

    [Fact]
    public void InlineImageIsEmbeddedImages()
    {
        PageComplexity c = OcrPageSelector.Evaluate(Page([TextItem.Of("cell text and more cell text", 40, 40, 500, 700)], new RectD(100, 100, 400, 300)));
        Assert.Contains(OcrReason.EmbeddedImages, c.Reasons);
        Assert.True(c.HasNativeText);
    }

    [Fact]
    public void FullPageImageWithNoTextReadsAsScanned()
    {
        PageComplexity c = OcrPageSelector.Evaluate(Page([], new RectD(0, 0, 612, 792)));
        Assert.Contains(OcrReason.Scanned, c.Reasons);
        Assert.True(c.FullPageImage);
        Assert.Equal(0, c.ImageCount);
        Assert.Null(c.UncoveredVectorArea);
        Assert.False(c.HasNativeText);
    }

    [Fact]
    public void TinyDecorationsAreIgnored()
    {
        PageComplexity c = OcrPageSelector.Evaluate(Page(DenseText(), new RectD(40, 40, 10, 10)));
        Assert.Equal(0, c.ImageCount);
        Assert.False(c.NeedsOcr);
    }

    [Fact]
    public void InvisibleLayerOnAScanIsReplaced()
    {
        PdfPageFacts page = new() { Width = 612, Height = 792, Items = DenseText(), Images = [new RectD(0, 0, 612, 792)], InvisibleShare = 0.95 };
        PageComplexity c = OcrPageSelector.Evaluate(page);
        Assert.Contains(OcrReason.InvisibleTextLayer, c.Reasons);
        Assert.True(c.ReplaceNative);
        Assert.False(c.HasNativeText);
    }

    [Fact]
    public void OutlinedTextIsVectorText()
    {
        PdfPageFacts page = new() { Width = 612, Height = 792, Items = DenseText(), FilledPaths = [new RectD(50, 820, 200, 12)] };
        Assert.Equal([OcrReason.VectorText], OcrPageSelector.Evaluate(page).Reasons);
    }

    [Fact]
    public void CappedPageRendersToExactlyTheCap()
    {
        static int Px(double pt, double dpi) => (int)Math.Round(pt * (dpi / 72.0), MidpointRounding.AwayFromZero);
        (double W, double H)[] sizes = [(612, 792), (595.28, 841.89), (612, 1008), (960, 540), (226.77, 2834.65), (2592, 3456)];
        foreach (int cap in new[] { 300, 1600, 2400, 4000 })
        {
            foreach ((double w, double h) in sizes)
            {
                double dpi = OcrPageSelector.FittingDpi(w, h, 300, cap);
                int longEdge = Math.Max(Px(w, dpi), Px(h, dpi));
                if (dpi < 300)
                {
                    Assert.Equal(cap, longEdge);
                }
                else
                {
                    Assert.True(longEdge <= cap);
                }
            }
        }

        Assert.Equal(150, OcrPageSelector.FittingDpi(612, 792, 150, 2400));
        Assert.Equal(300, OcrPageSelector.FittingDpi(612, 792, 300, 0));
        Assert.Equal(300, OcrPageSelector.FittingDpi(0, 0, 300, 2400));
    }

    [Theory]
    [InlineData(new float[] { 0, 0, 100, 0, 100, 20, 0, 20 }, 0)]
    [InlineData(new float[] { 10, 100, 10, 0, 30, 0, 30, 100 }, 90)]
    [InlineData(new float[] { 10, 0, 10, 100, 30, 100, 30, 0 }, 270)]
    [InlineData(new float[] { 0, 0, 20, 0, 20, 22, 0, 22 }, 0)]
    [InlineData(new float[] { 100, 20, 0, 20, 0, 0, 100, 0 }, 180)]
    public void PolygonRotation(float[] p, double expected) => Assert.Equal(expected, OcrMerge.PolygonRotationDeg(Quad(p)));

    [Fact]
    public void PolygonRotationScreenAxisVertical()
    {
        double r = OcrMerge.PolygonRotationDeg(Quad([20, 50, 50, 50, 50, 750, 20, 750]));
        Assert.True(r is 90 or 270, $"got {r}");
    }

    private static PointF[] Quad(float[] p) => [new(p[0], p[1]), new(p[2], p[3]), new(p[4], p[5]), new(p[6], p[7])];

    [Theory]
    [InlineData("44520]", "44520")]
    [InlineData("|123", "123")]
    [InlineData("0.3|", "0.3")]
    [InlineData("(note)", "(note)")]
    [InlineData("|hello|", "|hello|")]
    [InlineData("N/A", "N/A")]
    [InlineData("", "")]
    [InlineData("|||", "|||")]
    [InlineData("(78,939)", "(78,939)")]
    [InlineData("(12.5%)", "(12.5%)")]
    [InlineData("(78,939)|", "(78,939)")]
    [InlineData("|(78,939)]", "(78,939)")]
    [InlineData("78,939)", "78,939")]
    [InlineData("(78,939", "78,939")]
    [InlineData("   ", "")]
    [InlineData(" 123 ", "123")]
    public void CleanOcrTableArtifacts(string input, string expected) => Assert.Equal(expected, OcrMerge.CleanOcrTableArtifacts(input));

    [Theory]
    [InlineData("|", 4.3, 12.5, true)]
    [InlineData("I", 1.0, 7.2, true)]
    [InlineData("|  |", 3.0, 12.0, true)]
    [InlineData("—", 20.0, 3.0, true)]
    [InlineData("  ", 1.0, 1.0, true)]
    [InlineData("1", 5.0, 12.0, false)]
    [InlineData("I", 4.0, 10.0, false)]
    [InlineData("If", 1.0, 12.0, false)]
    [InlineData("N/A", 12.0, 8.0, false)]
    public void RuleArtifactCatchesBarsAndDecoration(string text, double w, double h, bool expected)
        => Assert.Equal(expected, OcrMerge.IsRuleArtifact(text, w, h));

    [Fact]
    public void OverlapsExistingText()
    {
        List<TextItem> items = [Item(10, 10, 20, 5)];
        Assert.True(OcrMerge.OverlapsExistingText(items, 12, 11, 5, 2, 2));
        Assert.False(OcrMerge.OverlapsExistingText(items, 100, 100, 5, 5, 2));
        Assert.True(OcrMerge.OverlapsExistingText(items, 31, 10, 5, 5, 2));
        Assert.False(OcrMerge.OverlapsExistingText(items, 35, 10, 5, 5, 2));
        Assert.False(OcrMerge.OverlapsExistingText([], 0, 0, 1, 1, 0));
    }

    [Fact]
    public void UncoveredPathArea()
    {
        RectD path = new(50, 300, 200, 12);
        double area = OcrMerge.UncoveredPathArea([path], []);
        Assert.InRange(area, 2399, 2401);
        Assert.True(area >= OcrPageSelector.UncoveredVectorAreaThreshold);
        Assert.Equal(0, OcrMerge.UncoveredPathArea([path], [Item(40, 295, 250, 25)]));
        Assert.InRange(OcrMerge.UncoveredPathArea([new RectD(0, 0, 100, 10)], [Item(0, 0, 50, 10)]), 499, 501);
        Assert.True(OcrMerge.UncoveredPathArea([new RectD(10, 10, 8, 8), new RectD(10, 30, 8, 8)], []) < OcrPageSelector.UncoveredVectorAreaThreshold);
    }

    [Fact]
    public void UnusableNativeUnicodeMapError()
    {
        TextItem item = Item(0, 0, 10, 10);
        Assert.False(Garble.IsUnusableNative(item));
        item.UnicodeMapError = true;
        Assert.True(Garble.IsUnusableNative(item));
    }

    [Fact]
    public void NativeWidthCoverageMergesOverlappingSpans()
    {
        List<TextItem> items = [Item(0, 0, 10, 10), Item(5, 0, 10, 10)];
        Assert.Equal(0.15, OcrMerge.NativeWidthCoverage(items, 0, 0, 100, 10), 4);
        Assert.Equal(0, OcrMerge.NativeWidthCoverage(items, 0, 20, 100, 10));
    }

    [Fact]
    public void FigureOcrWithShortWordsIsKept()
    {
        List<TextItem> natives = [TextItem.Of("Guide de Performance", 10, 92, 60, 4)];
        static OcrLine Word(string text, float x, float y) => new(text, new BoxF(x, y, x + 8, y + 4), [], 0.9f, 0, "latin");
        OcrLine[] lines =
        [
            Word("Logement", 10, 10), Word("et", 20, 10), Word("de", 30, 10), Word("la", 40, 10),
            Word("ES", 10, 20), Word("à", 20, 20), Word("Abordable", 30, 20),
        ];
        OcrMergeResult r = OcrMerge.Merge(natives, lines, 1.0, hasNativeText: true, replaceNative: false);
        List<string> texts = [.. r.Items.Select(i => i.Text)];
        foreach (string w in new[] { "Logement", "et", "de", "la", "ES", "à", "Abordable" })
        {
            Assert.Contains(w, texts);
        }

        Assert.Contains("Guide de Performance", texts);
    }

    [Fact]
    public void OcrOverlappingNativeTextIsDropped()
    {
        List<TextItem> natives = [TextItem.Of("Native line of text", 10, 10, 100, 10, "Helvetica")];
        OcrLine[] lines =
        [
            new("Native line of text", new BoxF(10, 10, 110, 20), [], 0.95f, 0, "latin"),
            new("Figure label", new BoxF(10, 60, 80, 70), [], 0.95f, 0, "latin"),
            new("low", new BoxF(10, 90, 40, 100), [], 0.05f, 0, "latin"),
        ];
        OcrMergeResult r = OcrMerge.Merge(natives, lines, 1.0, hasNativeText: true, replaceNative: false);
        Assert.Equal(["Native line of text", "Figure label"], r.Items.Select(i => i.Text));
        Assert.Equal(0.95, r.Ocr[0].Item.Confidence);
    }

    [Fact]
    public void ReplaceNativeDropsTheTextLayer()
    {
        List<TextItem> natives = [TextItem.Of("Old OCR layer", 10, 10, 100, 10, "GlyphLessFont")];
        OcrLine[] lines = [new("New OCR text", new BoxF(10, 10, 110, 20), [], 0.9f, 0, "latin")];
        OcrMergeResult r = OcrMerge.Merge(natives, lines, 1.0, hasNativeText: false, replaceNative: true);
        Assert.Equal(["New OCR text"], r.Items.Select(i => i.Text));
        Assert.Single(r.DroppedNatives);
    }
}
