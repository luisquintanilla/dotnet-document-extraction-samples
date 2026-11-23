using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig;

namespace OcriaNano.DocumentExtraction.Tests.Document;

/// <summary>End-to-end runs over the sample books with the installed models.</summary>
public class ExtractionEndToEndTests
{
    public static TheoryData<string> Books => ["agents-propose-tools-decide", "tensors-in-ten-pages", "tokens-explained"];

    private static async Task<ExtractionDocument> Extract(string path, string mediaType, DocumentExtractionOptions? options = null)
    {
        TestModels.Require();
        using OcriaNanoDocumentExtractionClient client = new();
        await using FileStream fs = File.OpenRead(path);
        return await client.ExtractAsync(fs, Path.GetFileName(path), mediaType, options, TestContext.Current.CancellationToken);
    }

    private static string Book(string name) => TestModels.RepoFile("books", "pdf", name + ".pdf");

    /// <summary>The plain text of every element (no Markdown markers), in document order.</summary>
    internal static IEnumerable<string> ElementTexts(ExtractionDocument doc)
    {
        foreach (ExtractionDocumentElement e in doc.Sections.SelectMany(s => s.Elements))
        {
            switch (e)
            {
                case ExtractionDocumentImageElement img:
                    yield return img.Text ?? "";
                    break;
                case ExtractionDocumentTableElement t:
                    foreach (ExtractionDocumentTableCell? c in t.Cells)
                    {
                        if (c?.Content?.Content is TextContent tc)
                        {
                            yield return tc.Text;
                        }
                    }

                    break;
                default:
                    yield return (e.Content as TextContent)?.Text ?? "";
                    break;
            }
        }
    }

    private static Dictionary<string, int> Bag(IEnumerable<string> words)
        => words.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());

    private static IEnumerable<string> Words(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]).ToLowerInvariant())
            .Where(w => w.Length > 0);

    /// <summary>Bag-of-words F1 between two texts.</summary>
    internal static double WordF1(string expected, string actual)
    {
        (int common, int ne, int na) = Overlap(expected, actual);
        return ne + na == 0 ? 1 : 2.0 * common / (ne + na);
    }

    /// <summary>Fraction of <paramref name="expected"/>'s words (with multiplicity) found in <paramref name="actual"/>.</summary>
    internal static double WordRecall(string expected, string actual)
    {
        (int common, int ne, _) = Overlap(expected, actual);
        return ne == 0 ? 1 : (double)common / ne;
    }

    private static (int Common, int Expected, int Actual) Overlap(string expected, string actual)
    {
        Dictionary<string, int> e = Bag(Words(expected)), a = Bag(Words(actual));
        return (e.Sum(kv => Math.Min(kv.Value, a.GetValueOrDefault(kv.Key))), e.Values.Sum(), a.Values.Sum());
    }

    private static string PdfWords(PdfDocument pdf, int? page = null)
        => string.Join("\n", (page is int p ? [pdf.GetPage(p)] : pdf.GetPages()).Select(pg => string.Join(" ", pg.GetWords().Select(w => w.Text))));

    private static string Letters(IEnumerable<string> texts) => string.Concat(texts.SelectMany(t => t.Where(char.IsLetterOrDigit)).Order());

    [Theory]
    [MemberData(nameof(Books))]
    public async Task BornDigitalBookKeepsItsTextLayer(string name)
    {
        ExtractionDocument doc = await Extract(Book(name), "application/pdf");
        Assert.Equal(0, doc.Metadata[ExtractionDocumentKeys.PagesOcred]);
        Assert.False(doc.Metadata.ContainsKey(ExtractionDocumentKeys.MeanConfidence));

        using PdfDocument pdf = PdfDocument.Open(Book(name));
        Assert.Equal(pdf.NumberOfPages, doc.SourcePages.Count);
        Assert.All(doc.SourcePages, p =>
        {
            Assert.Equal(ExtractionDocumentCoordinateUnit.Point, p.CoordinateUnit);
            Assert.Equal(ExtractionDocumentCoordinateOrigin.TopLeft, p.CoordinateOrigin);
        });

        // Same characters as PdfPig's text layer, in any order and modulo whitespace.
        string expected = Letters(pdf.GetPages().Select(p => p.Text));
        Assert.Equal(expected, Letters(ElementTexts(doc)));
        Assert.All(doc.Sections.SelectMany(s => s.Elements), e => Assert.All(e.Sources, s => Assert.Null(s.GetRawRepresentation())));
    }

    [Theory]
    [MemberData(nameof(Books))]
    public async Task ScannedBookIsOcrdAndReadsLikeItsTwin(string name)
    {
        ExtractionDocument scanned = await Extract(Book(name + "-scanned"), "application/pdf");
        using PdfDocument pdf = PdfDocument.Open(Book(name));
        Assert.Equal(pdf.NumberOfPages, scanned.Metadata[ExtractionDocumentKeys.PagesOcred]);
        Assert.InRange((double)scanned.Metadata[ExtractionDocumentKeys.MeanConfidence]!, 0.9, 1.0);

        // The scans also carry figure text the born-digital text layer doesn't have, so recall is the gate and F1 is reported.
        string truth = PdfWords(pdf), ocr = string.Join("\n", ElementTexts(scanned));
        double recall = WordRecall(truth, ocr), f1 = WordF1(truth, ocr);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}-scanned vs born-digital: word recall {recall:0.0000}, F1 {f1:0.0000}");
        Assert.True(recall >= 0.95, $"word recall {recall:0.0000}");

        ExtractionDocumentSourceReference src = scanned.Sections.SelectMany(s => s.Elements).SelectMany(e => e.Sources).First();
        Assert.Equal(4, src.Polygon.Count);
        Assert.IsType<double>(src.GetRawRepresentation());
    }

    [Fact]
    public async Task ImageIsOcrdInPixels()
    {
        string png = TestModels.RepoFile("books", "pages", "tokens-explained-hero.png");
        ExtractionDocument doc = await Extract(png, "image/png");
        ExtractionDocumentSourcePage page = Assert.Single(doc.SourcePages);
        Assert.Equal(ExtractionDocumentCoordinateUnit.Pixel, page.CoordinateUnit);
        using (SkiaSharp.SKBitmap bmp = SkiaSharp.SKBitmap.Decode(png))
        {
            Assert.Equal(bmp.Width, page.Dimensions!.Value.Width, 3);
            Assert.Equal(bmp.Height, page.Dimensions!.Value.Height, 3);
        }

        Assert.Equal(1, doc.Metadata[ExtractionDocumentKeys.PagesOcred]);
        using PdfDocument pdf = PdfDocument.Open(TestModels.RepoFile("books", "pages", "tokens-explained-hero.pdf"));
        double recall = WordRecall(PdfWords(pdf), string.Join("\n", ElementTexts(doc)));
        TestContext.Current.TestOutputHelper?.WriteLine($"tokens-explained-hero.png word recall: {recall:0.0000}");
        Assert.True(recall >= 0.95, $"word recall {recall:0.0000}");
    }

    [Fact]
    public async Task TablePageHasRolesHeadingsAndSpans()
    {
        ExtractionDocument doc = await Extract(Book("tokens-explained"), "application/pdf", new() { AdditionalProperties = { ["ocria.pages"] = "1-3" } });
        Assert.Equal([1, 2, 3], doc.SourcePages.Select(p => p.PageNumber!.Value));
        Assert.Equal("ran", doc.Metadata[ExtractionDocumentKeys.Layout]);
        List<ExtractionDocumentElement> elements = [.. doc.Sections.SelectMany(s => s.Elements)];

        List<ExtractionDocumentTextElement> titles = [.. elements.OfType<ExtractionDocumentTextElement>().Where(e => e.Role == "title")];
        Assert.NotEmpty(titles);
        Assert.All(titles, t => Assert.Equal(1, t.HeadingLevel));
        Assert.Contains(elements.OfType<ExtractionDocumentTextElement>(), e => e.Role == "heading" && e.HeadingLevel == 2);
        Assert.Contains(elements.OfType<ExtractionDocumentTextElement>(), e => e.Role == "pageFooter");

        List<ExtractionDocumentTableElement> tables = [.. elements.OfType<ExtractionDocumentTableElement>()];
        Assert.NotEmpty(tables);
        List<ExtractionDocumentTableCell> cells = [.. tables.SelectMany(t => t.Cells.Cast<ExtractionDocumentTableCell?>()).OfType<ExtractionDocumentTableCell>()];
        Assert.Contains(cells, c => c.Role == "columnHeader");
        Assert.All(cells, c => Assert.True(c.RowSpan >= 1 && c.ColumnSpan >= 1));
        Assert.All(cells.Where(c => c.Content is not null), c => Assert.NotEmpty(c.Sources));

        string md = Assert.IsType<TextContent>(doc.Content).Text;
        Assert.Contains("# ", md);
        Assert.Contains("| --- |", md);
    }

    [Fact]
    public async Task SpanningTableCellsBecomeHtml()
    {
        // The figure on page 2 is a raster diagram whose header row spans the token columns.
        ExtractionDocument doc = await Extract(Book("tokens-explained-scanned"), "application/pdf", new() { AdditionalProperties = { ["ocria.pages"] = "2" } });
        ExtractionDocumentTableElement table = Assert.Single(doc.Sections.SelectMany(s => s.Elements).OfType<ExtractionDocumentTableElement>());
        ExtractionDocumentTableCell spanning = Assert.Single(table.Cells.Cast<ExtractionDocumentTableCell?>().OfType<ExtractionDocumentTableCell>(), c => c.ColumnSpan > 1);
        Assert.Equal("columnHeader", spanning.Role);
        Assert.Contains("colspan=", Assert.IsType<TextContent>(doc.Content).Text);
    }

    [Fact]
    public async Task OverridesSwitchOffOcrAndLayout()
    {
        ExtractionDocument doc = await Extract(Book("tokens-explained-scanned"), "application/pdf", new()
        {
            AdditionalProperties = { ["ocria.ocr"] = "never", ["ocria.layout"] = false, ["ocria.pages"] = "2" },
        });
        Assert.Equal(0, doc.Metadata[ExtractionDocumentKeys.PagesOcred]);
        Assert.StartsWith("skipped: disabled", (string)doc.Metadata[ExtractionDocumentKeys.Layout]!);
    }

    [Fact]
    public async Task AlwaysOcrsBornDigitalPagesWithHeuristicParagraphs()
    {
        ExtractionDocument doc = await Extract(Book("tokens-explained"), "application/pdf", new()
        {
            AdditionalProperties = { ["ocria.ocr"] = "always", ["ocria.layout"] = "false", ["ocria.pages"] = "4", ["ocria.dpi"] = 200 },
        });
        Assert.Equal(1, doc.Metadata[ExtractionDocumentKeys.PagesOcred]);
        Assert.All(doc.Sections.SelectMany(s => s.Elements), e => Assert.Equal("paragraph", Assert.IsType<ExtractionDocumentTextElement>(e).Role));
        using PdfDocument pdf = PdfDocument.Open(Book("tokens-explained"));
        Assert.True(WordRecall(PdfWords(pdf, 4), string.Join("\n", ElementTexts(doc))) >= 0.95);
    }

    [Fact]
    public async Task UnsupportedMediaTypeThrows()
    {
        using OcriaNanoDocumentExtractionClient client = new();
        using MemoryStream ms = new([1, 2, 3]);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ExtractAsync(ms, "x.gif", "image/gif", cancellationToken: TestContext.Current.CancellationToken));
    }
}

internal static class ExtractionDocumentKeys
{
    public const string PagesOcred = "ocria.pagesOcred";
    public const string MeanConfidence = "ocria.meanConfidence";
    public const string Layout = "ocria.layout";
}
