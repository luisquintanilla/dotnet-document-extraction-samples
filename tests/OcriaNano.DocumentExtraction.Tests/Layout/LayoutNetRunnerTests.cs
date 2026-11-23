using Microsoft.ML.OnnxRuntime;
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Layout;
using UglyToad.PdfPig;

namespace OcriaNano.DocumentExtraction.Tests.Layout;

public class LayoutNetRunnerTests
{
    [Fact]
    public void RunsOnPdfPagesAndFindsTables()
    {
        string dir = TestModels.Require();
        string model = Path.Combine(dir, "tablenet.onnx");
        if (!File.Exists(model))
        {
            Assert.Skip("tablenet.onnx not installed.");
        }

        using InferenceSession session = new(model);
        LayoutNetRunner runner = new(session);
        string[] pdfs = [.. Directory.GetFiles(TestModels.RepoFile("books", "pdf"), "*.pdf").Where(p => !p.Contains("scanned"))];
        int tables = 0, blocks = 0, pages = 0;
        foreach (string pdf in pdfs)
        {
            byte[] bytes = File.ReadAllBytes(pdf);
            using PdfDocument doc = PdfDocument.Open(bytes);
            for (int i = 0; i < Math.Min(doc.NumberOfPages, 6); i++)
            {
                var page = doc.GetPage(i + 1);
                var (units, w, h) = PdfUnits.FromPage(page);
                using var bmp = PDFtoImage.Conversion.ToImage(bytes, i, options: new(Dpi: 150));
                using var bgra = bmp.ColorType == SkiaSharp.SKColorType.Bgra8888 ? null : bmp.Copy(SkiaSharp.SKColorType.Bgra8888);
                var src = bgra ?? bmp;
                Bgra32Image raster = new(src.Width, src.Height, src.Bytes);
                PageModel pm = runner.Run(new LayoutPageInput(units, w, h, false, raster), TestContext.Current.CancellationToken);
                pages++;
                tables += pm.Tables.Count;
                blocks += pm.Blocks.Count;
                foreach (ModelTable t in pm.Tables)
                {
                    Assert.True(t.Grid.NCols >= 2);
                    Assert.NotEmpty(t.Grid.Cells);
                }
            }
        }

        Assert.True(pages > 0);
        Assert.True(blocks > 0, "the model should label page blocks");
        Assert.True(tables > 0, $"expected at least one table across {pages} pages");
    }
}
