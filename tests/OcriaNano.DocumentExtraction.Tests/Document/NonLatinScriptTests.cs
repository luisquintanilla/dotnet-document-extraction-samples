using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace OcriaNano.DocumentExtraction.Tests.Document;

/// <summary>Pages in other scripts, rendered with a system font, routed to their recognizers and read back.</summary>
public class NonLatinScriptTests
{
    public static TheoryData<string, OcriaScripts, string, string[]> Pages => new()
    {
        {
            "chinese", OcriaScripts.Cjk, "cjk",
            ["今天天气很好我们一起去公园散步", "图书馆每天早上八点开门", "这本书讲的是语言模型如何读取文字", "请把文件放在桌子上面"]
        },
        {
            "japanese", OcriaScripts.Cjk, "cjk",
            ["東京は日本の首都です", "私は毎朝コーヒーを飲みます", "この本はとても面白いと思います", "駅まで歩いて十分かかります"]
        },
        {
            "russian", OcriaScripts.Latin, "latin",
            ["Съешь же ещё этих мягких французских булок", "Библиотека открыта каждый день с утра", "Модель читает текст на странице", "Положите документы на стол"]
        },
        {
            "greek", OcriaScripts.Latin, "latin",
            ["Η βιβλιοθήκη είναι ανοιχτή κάθε μέρα", "Το μοντέλο διαβάζει το κείμενο", "Καλημέρα σε όλους τους φίλους", "Αυτό το βιβλίο είναι πολύ καλό"]
        },
        {
            "hindi", OcriaScripts.World, "world",
            ["पुस्तकालय हर दिन सुबह खुलता है", "मॉडल पृष्ठ पर पाठ पढ़ता है", "किताब बड़ी मेज पर रखी है", "हम हर दिन पढ़ना पसंद करते हैं"]
        },
        {
            "thai", OcriaScripts.World, "world",
            ["ห้องสมุดเปิดทุกวันตอนเช้า", "แบบจำลองอ่านข้อความบนหน้า", "หนังสืออยู่บนโต๊ะใหญ่", "เราชอบอ่านหนังสือทุกวัน"]
        },
        {
            "georgian", OcriaScripts.World, "world",
            ["ბიბლიოთეკა ყოველდღე დილით იხსნება", "მოდელი კითხულობს ტექსტს გვერდზე", "წიგნი დიდ მაგიდაზეა", "ჩვენ გვიყვარს კითხვა ყოველდღე"]
        },
        {
            "arabic", OcriaScripts.Rtl, "rtl",
            ["مرحبا بكم في المكتبة العامة", "يقرأ النموذج النص في الصفحة", "الكتاب على الطاولة الكبيرة", "نحن نحب القراءة كل يوم"]
        },
        {
            "hebrew", OcriaScripts.Rtl, "rtl",
            ["ברוכים הבאים לספרייה הציבורית", "המודל קורא את הטקסט בעמוד", "הספר נמצא על השולחן", "אנחנו אוהבים לקרוא כל יום"]
        },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task PageIsRoutedToItsRecognizer(string name, OcriaScripts script, string group, string[] lines)
    {
        string dir = TestModels.Require();
        if (!File.Exists(Path.Combine(dir, $"rec_{group}.onnx")))
        {
            Assert.Skip($"rec_{group}.onnx not installed; run tools/ocria/Get-OcriaModels.ps1 -Scripts {group} -AcceptLicense.");
        }

        byte[] png = Render(lines);
        ExtractionDocument routed = await Extract(png, script);
        ExtractionDocument latinOnly = await Extract(png, OcriaScripts.Latin);

        string truth = string.Concat(lines);
        double recall = CharRecall(truth, routed.GetText()), latinRecall = CharRecall(truth, latinOnly.GetText());
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: script={routed.Metadata[OcriaNano.DocumentExtraction.Document.ExtractionDocumentBuilder.ScriptKey]} char recall {recall:0.000} (latin only {latinRecall:0.000})");

        Assert.Contains(group, ((string)routed.Metadata[OcriaNano.DocumentExtraction.Document.ExtractionDocumentBuilder.ScriptKey]!).Split(','));
        Assert.True(recall >= 0.9, $"{name} char recall {recall:0.000}");
        if (script != OcriaScripts.Latin)
        {
            Assert.True(latinRecall < 0.5, $"{name} read by Latin alone: {latinRecall:0.000}");
        }
    }

    private static async Task<ExtractionDocument> Extract(byte[] png, OcriaScripts scripts)
    {
        using OcriaNanoDocumentExtractionClient client = new(new OcriaNanoOptions { Scripts = scripts });
        using MemoryStream ms = new(png);
        return await client.ExtractAsync(ms, "page.png", "image/png", null, TestContext.Current.CancellationToken);
    }

    /// <summary>Fraction of the truth's letters (with multiplicity, ignoring order and spaces) found in the reading.</summary>
    private static double CharRecall(string truth, string reading)
    {
        Dictionary<char, int> have = reading.Where(char.IsLetter).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        List<char> want = [.. truth.Where(char.IsLetter)];
        return (double)want.GroupBy(c => c).Sum(g => Math.Min(g.Count(), have.GetValueOrDefault(g.Key))) / want.Count;
    }

    /// <summary>Draws the lines black on white, shaped (joined Arabic, right-to-left order) by HarfBuzz.</summary>
    private static byte[] Render(string[] lines)
    {
        // The first face that has every glyph (fallback for 今 may be a Japanese font without simplified Chinese).
        string all = string.Concat(lines.SelectMany(l => l.Where(c => !char.IsWhiteSpace(c))));
        SKTypeface? face = all.EnumerateRunes().Select(r => r.Value).Distinct()
            .SelectMany(cp => new[] { SKFontManager.Default.MatchCharacter(cp), SKFontManager.Default.MatchCharacter(null, SKFontStyle.Normal, ["zh-Hans"], cp) })
            .FirstOrDefault(f => f is not null && f.ContainsGlyphs(all));
        if (face is null)
        {
            Assert.Skip($"No system font covers '{lines[0]}'.");
        }

        using SKTypeface owned = face;
        const int size = 40, w = 1400;
        using SKFont font = new(face, size);
        using SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true };
        using SKShaper shaper = new(face);
        using SKBitmap bmp = new(w, 120 + lines.Length * size * 2);
        using (SKCanvas canvas = new(bmp))
        {
            canvas.Clear(SKColors.White);
            for (int i = 0; i < lines.Length; i++)
            {
                canvas.DrawShapedText(shaper, lines[i], 60, 100 + i * size * 2, SKTextAlign.Left, font, paint);
            }
        }

        using SKImage img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
