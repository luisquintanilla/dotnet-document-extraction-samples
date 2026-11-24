using OcriaNano.DocumentExtraction.Ocr;

namespace OcriaNano.DocumentExtraction.Tests.Ocr;

// Ported from the #[cfg(test)] tests of LiteParse crates/liteparse/src/ocr/ocria/page.rs, Apache-2.0.
public class PageTests
{
    [Fact]
    public void HomoglyphsFoldOnlyInsideLatinWords()
        => Assert.Equal("pt6a-42 Привет", LatinCleanup.CleanLatin("pt6а-42 Привет"));

    [Theory]
    [InlineData("REPՍBLQՍE FRANÇAISE", "REPUBLQUE FRANÇAISE")]
    [InlineData("C๐mmunicatoทร Security", "Communicatons Security")]
    [InlineData("В - Linear 1- Center", "B - Linear 1- Center")]
    [InlineData("KLAR 08 (UA०८ SHIP TERM", "KLAR 08 (UA08 SHIP TERM")]
    [InlineData("GSTIN: 27ADC300६/१ZK", "GSTIN: 27ADC3006/1ZK")]
    [InlineData("ZQX272003тOKEN OнMS", "ZQX272003TOKEN OHMS")]
    [InlineData("airborлe пoise to гeduce", "airborne noise to reduce")]
    [InlineData("aрганізацый ФOPYMA ПяТAГΑ", "арганізацый ФОРУМА ПяТАГА")]
    [InlineData("significant at α = 0.05 level", "significant at α = 0.05 level")]
    public void CleanLatinFoldsOtherScriptsInLatinLines(string input, string expected)
        => Assert.Equal(expected, LatinCleanup.CleanLatin(input));

    [Theory]
    [InlineData("Привет мир, как дела")]
    [InlineData("ReturnToWorkSA ਦਾ ਨਿਯਮ ਸਮੂਹ (Regulation Group) ਰਿਟਰਨ ਟੂ ਵਰਕ")]
    [InlineData("३ मार्च २०२४ को")]
    [InlineData("การบริหารความเสี่ยง ๒๕๖๗")]
    [InlineData("Հայաստանի Հանրապետություն")]
    [InlineData("ติดตั้ง Windowsนี้ ได้ที่ร้าน")]
    public void CleanLatinLeavesOtherScriptLines(string input)
        => Assert.Equal(input, LatinCleanup.CleanLatin(input));

    [Fact]
    public void ReadingFrameMapsQuarterTurnBack()
    {
        PointF[] q = [new(0, 0), new(10, 0), new(10, 2), new(0, 2)];
        PointF[] r = PageFrame.ReadingFrame(q, flip: false, sideways: true, w0: 100);
        Assert.Equal(new PointF(99, 0), r[0]);
        Assert.Equal(new PointF(99, 10), r[1]);
    }

    [Fact]
    public void JunkNeedsLettersOrConfidence()
    {
        Assert.True(PageFrame.IsJunk("--", 0.99f));
        Assert.True(PageFrame.IsJunk("ab", 0.5f));
        Assert.False(PageFrame.IsJunk("ab", 0.9f));
        Assert.False(PageFrame.IsJunk("abc", 0.1f));
    }

    [Fact]
    public void BidiReordersHebrewVisualToLogical()
        => Assert.Equal("שלום", Bidi.VisualToLogical("םולש"));

    [Fact]
    public void LongTiltedLinesSetThePageSkewAndShortOnesDont()
    {
        static OcrLine Line(double deg, float len, float y)
        {
            (double s, double c) = Math.SinCos(deg * Math.PI / 180);
            PointF a = new(50, y), b = new(50 + (float)(len * c), y + (float)(len * s));
            PointF[] q = [a, b, new(b.X, b.Y + 20), new(a.X, a.Y + 20)];
            return new OcrLine("text", BoxF.Bounds(q), q, 0.9f, 0, "latin");
        }

        OcrLine[] tilted = [Line(5, 400, 100), Line(5, 500, 200), Line(5, 300, 300), Line(0, 40, 400)];
        Assert.Equal(5, Deskew.PageSkew(tilted)!.Value, 3);
        Assert.Null(Deskew.PageSkew(tilted[..2]));
        Assert.Null(Deskew.PageSkew([Line(0.1, 400, 100), Line(0.1, 400, 200), Line(0.1, 400, 300)]));

        OcrLine up = Deskew.Upright(tilted[0], 500, 500, 5);
        Assert.Equal(up.Quad[0].Y, up.Quad[1].Y, 2);
    }
}
