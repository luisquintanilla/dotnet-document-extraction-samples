using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Ocr;
using OcriaNano.DocumentExtraction.Onnx;
using PDFtoImage;
using SkiaSharp;

namespace OcriaNano.DocumentExtraction.Tests.Ocr;

public class OcriaEngineTests
{
    private static RgbImage ToRgb(SKBitmap bitmap)
    {
        using SKBitmap bgra = bitmap.Copy(SKColorType.Bgra8888);
        return new Bgra32Image(bgra.Width, bgra.Height, bgra.GetPixelSpan().ToArray()).ToRgb();
    }

    private static RgbImage RenderText(params string[] lines)
    {
        using SKBitmap bitmap = new(1100, (120 * lines.Length) + 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.White);
        using SKFont font = new(SKTypeface.Default, 40);
        using SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true };
        for (int i = 0; i < lines.Length; i++)
        {
            canvas.DrawText(lines[i], 40, 80 + (i * 120), font, paint);
        }

        return ToRgb(bitmap);
    }

    private static OcriaEngine CreateEngine(string dir) => new(new SessionPool(dir, verifyHashes: false, sessionOptions: null), OcriaScripts.Latin);

    [Fact]
    public void RecognizesRenderedText()
    {
        OcriaEngine engine = CreateEngine(TestModels.Require());
        OcrPage page = engine.Recognize(RenderText("The quick brown fox jumps over", "the lazy dog 12345"), TestContext.Current.CancellationToken);
        string text = string.Join("\n", page.Lines.Select(l => l.Text));
        Assert.Contains("quick", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lazy", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12345", text);
        Assert.All(page.Lines, l => Assert.Equal("latin", l.Script));
    }

    [Fact]
    public void BlankPageHasNoLines()
    {
        OcriaEngine engine = CreateEngine(TestModels.Require());
        RgbImage blank = new(400, 300, [.. Enumerable.Repeat((byte)255, 400 * 300 * 3)]);
        Assert.Empty(engine.Recognize(blank, TestContext.Current.CancellationToken).Lines);
    }

    [Fact]
    public void ScannedPdfPageReadsAsWords()
    {
        string dir = TestModels.Require();
        string pdf = TestModels.RepoFile("books", "pdf", "tokens-explained-scanned.pdf");
        if (!File.Exists(pdf))
        {
            Assert.Skip("scanned sample PDF not found");
        }

        OcriaEngine engine = CreateEngine(dir);
        using FileStream stream = File.OpenRead(pdf);
        using SKBitmap bitmap = Conversion.ToImage(stream, page: 0, options: new RenderOptions(Dpi: 200));
        OcrPage page = engine.Recognize(ToRgb(bitmap), TestContext.Current.CancellationToken);
        Assert.True(page.Lines.Count >= 5, $"only {page.Lines.Count} lines");
        string text = string.Join(" ", page.Lines.Select(l => l.Text));
        Assert.True(text.Count(char.IsLetter) > 100, text);
    }
}
