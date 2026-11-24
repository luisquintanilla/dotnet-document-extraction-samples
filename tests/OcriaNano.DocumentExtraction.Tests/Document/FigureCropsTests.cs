using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using OcriaNano.DocumentExtraction.Document;
using OcriaNano.DocumentExtraction.Layout;
using OcriaNano.DocumentExtraction.Ocr;
using OcriaNano.DocumentExtraction.Pdf;
using SkiaSharp;

namespace OcriaNano.DocumentExtraction.Tests.Document;

public class FigureCropsTests
{
    private static readonly Box4 Caption = new(100, 300, 200, 320);

    private static PageResult Page(params ModelBlock[] blocks)
    {
        Unit u = new() { Text = "Caption", PBox = Caption, Bx = Caption, H = 20 };
        return new PageResult
        {
            Number = 1,
            Width = 600,
            Height = 800,
            Units = [u],
            Sources = [UnitSource.Rect(Caption)],
            Model = new PageModel([], blocks),
        };
    }

    private static ModelBlock Figure(string text = "Caption") => new(Caption, LayoutClass.FigureText, text, [0]);

    [Fact]
    public void RegionTakesInImagesNextToTheTextButNotFarOrPageSizedOnes()
    {
        RectD above = new(110, 200, 80, 95);   // 5 pt above the caption: a thumbnail
        RectD far = new(400, 200, 80, 95);
        RectD background = new(0, 0, 600, 800);
        Box4 r = FigureCrops.Region(Page(), Caption, [above, far, background]);
        Assert.Equal(new Box4(100, 200, 200, 320), r);
    }

    [Fact]
    public void RegionIsTurnedBackOntoATiltedPage()
    {
        PageResult tilted = new()
        {
            Number = 1,
            Width = 600,
            Height = 800,
            Deskew = 3,
            DeskewCenter = (300, 400),
        };
        IReadOnlyList<(double X, double Y)> corners = Deskew.RotateBack(UnitSource.Rect(Caption).Polygon, 300, 400, 3);
        Box4 r = FigureCrops.Region(tilted, Caption, []);
        Assert.Equal(corners.Min(p => p.X), r.X0, 9);
        Assert.Equal(corners.Max(p => p.Y), r.Y1, 9);
        Assert.True(r.Height > Caption.Height);
    }

    [Fact]
    public void CropIsThePaddedRegionAtTheRasterScaleAndSkipsOtherBlocks()
    {
        ModelBlock fig = Figure();
        ModelBlock text = new(new Box4(100, 400, 300, 420), LayoutClass.Text, "Body", [0]);
        ModelBlock blank = Figure("  ");
        using SKBitmap bmp = new(1200, 1600);   // 2 px per point
        IReadOnlyDictionary<ModelBlock, byte[]> crops = FigureCrops.Crop(Page(fig, text, blank), [], bmp);

        byte[] png = Assert.Single(crops, kv => ReferenceEquals(kv.Key, fig)).Value;
        Assert.Single(crops);
        using SKBitmap crop = SKBitmap.Decode(png);
        double pad = FigureCrops.PaddingPt;
        Assert.Equal((int)((Caption.Width + 2 * pad) * 2), crop.Width);
        Assert.Equal((int)((Caption.Height + 2 * pad) * 2), crop.Height);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "true", true)]
    [InlineData(true, false, false)]
    public void FigureImagesFollowTheOptionAndThePerCallKey(bool option, object? perCall, bool expected)
    {
        Dictionary<string, object?>? props = perCall is null ? null : new() { [OcriaNanoDocumentExtractionClient.FigureImagesProperty] = perCall };
        Assert.Equal(expected, OcriaNanoDocumentExtractionClient.Settings(new() { IncludeFigureImages = option }, props).FigureImages);
    }

    [Fact]
    public void BuilderPutsTheCropOnTheImageElement()
    {
        ModelBlock fig = Figure();
        PageResult withCrop = Page(fig);
        withCrop.FigureImages = new Dictionary<ModelBlock, byte[]>(ReferenceEqualityComparer.Instance) { [fig] = [1, 2, 3] };

        ExtractionDocumentImageElement Image(PageResult p)
            => Assert.Single(ExtractionDocumentBuilder.Build("doc", [p]).Sections.SelectMany(s => s.Elements).OfType<ExtractionDocumentImageElement>());

        DataContent data = Assert.IsType<DataContent>(Image(withCrop).Content);
        Assert.Equal("image/png", data.MediaType);
        Assert.Equal([1, 2, 3], data.Data.ToArray());
        Assert.Equal("Caption", Image(withCrop).Text);
        Assert.Null(Image(Page(fig)).Content);
    }
}
