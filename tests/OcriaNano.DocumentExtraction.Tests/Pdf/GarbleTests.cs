using OcriaNano.DocumentExtraction.Pdf;

namespace OcriaNano.DocumentExtraction.Tests.Pdf;

// Ported from LiteParse crates/liteparse/src/ocr_merge.rs tests, Apache-2.0.
public class GarbleTests
{
    private static readonly string[] GarbledRows =
    [
        "Tepx Nzop Opdnctaetzy Bflyetej Cpgtph Olep",
        "KI-334 Cpawlnpxpye Qtwepc Lddpxmwj 44 5359-30-34",
        "KI-335 Azcelmwp Dpydzc Wzfdtyr 45 5359-30-35",
        "KI-336 Nlwtmcletzy Mclnvpe 46 5359-30-36",
    ];

    private static readonly string[] HealthyParagraph =
    [
        "This fictional report is a public parser-routing fixture.",
        "The upper section uses a standard font and extracts normally.",
        "All names, identifiers, quantities, and dates are fabricated.",
        "A PDF viewer still renders both sections as readable text.",
    ];

    private static readonly string[] Vietnamese =
    [
        "Trường học tổ chức buổi họp phụ huynh vào sáng thứ bảy tuần trước.",
        "Những người tham dự đã thảo luận về chương trình học kỳ mới.",
        "Thầy hiệu trưởng nhấn mạnh việc rèn luyện thể chất cho học sinh.",
        "Cuối buổi, phụ huynh được nhận tài liệu hướng dẫn chi tiết.",
    ];

    private static readonly string[] Pinyin =
    [
        "Rénrén shēng ér zìyóu, zài zūnyán hé quánlì shàng yīlǜ píngděng.",
        "Tāmen fùyǒu lǐxìng hé liángxīn, bìng yīng yǐ xiōngdì guānxì de jīngshén xiāng duìdài.",
    ];

    private static List<TextItem> Page(params (string? Font, string Text)[] items)
        => [.. items.Select(i => TextItem.Of(i.Text, 0, 0, 10, 10, i.Font))];

    private static (string?, string)[] With(string? font, IEnumerable<string> texts) => [.. texts.Select(t => (font, t))];

    [Theory]
    [InlineData("hZeY")]
    [InlineData("cVdaVTe")]
    [InlineData("bViZgAVah")]
    [InlineData("ÞkusLkkykuÚke")]
    [InlineData("iNdm;s;=udf.a")]
    [InlineData("ࡶڜటర")]
    [InlineData("ประสงคŤ")]
    public void MalformedTokensFlagCipherAndLegacyFonts(string token) => Assert.True(Garble.TokenIsMalformed(token));

    [Theory]
    [InlineData("DataEncoder")]
    [InlineData("ShannonOverbrook")]
    [InlineData("iPhone")]
    [InlineData("McDonald")]
    [InlineData("fact_api.FactApi")]
    [InlineData("ExpRatio_Excluded")]
    [InlineData("iPhone発売")]
    [InlineData("Washington")]
    [InlineData("1G1FJ3D6&H")]
    [InlineData("W3GG")]
    [InlineData("ἀρχῇ")]
    [InlineData("ⲁⲣⲭⲏ")]
    [InlineData("ሀገርሰላም")]
    [InlineData("Ϣⲁⲣⲉ")]
    [InlineData("հոկտեմբերի")]
    [InlineData("Հանրապետությունում")]
    [InlineData("བསྟན་པའི")]
    public void WellFormedTokensPass(string token) => Assert.False(Garble.TokenIsMalformed(token));

    [Fact]
    public void ShortTokenIsNotJudged() => Assert.Null(Garble.TokenIsMalformed("ab"));

    [Fact]
    public void ControlCharsFlagBrokenMapsNotDingbats()
    {
        Assert.Equal((0, 17), Garble.ControlChars("Plain text\twith tabs\n"));
        List<TextItem> page = Page(("BrokenFont", "$SULO \u0015\u0015\u000f \u0015\u0013\u0015\u0018 \u0010 3DJH"));
        Assert.Equal(GarbledKind.WholePage, Garble.Scope(page).Kind);
        Assert.True(Garble.IsSymbolFont("ZQNKFZ+Wingdings2"));
        Assert.True(Garble.IsSymbolFont("VTJFIN+Webdings"));
        Assert.False(Garble.IsSymbolFont("ArialMT"));
        Assert.True(Garble.IsSymbolFont("ABCDEF+CMEX10"));
        Assert.True(Garble.IsSymbolFont("Universal-GreekwithMathPi"));
        Assert.False(Garble.IsSymbolFont("TimesNewRomanPS-ItalicMT"));
    }

    [Fact]
    public void AccentedVowelsAreNotGarbled()
    {
        foreach (string[] para in new[] { Vietnamese, Pinyin })
        {
            List<TextItem> page = Page(With("ArialUnicodeMS", para));
            Assert.Equal(GarbledKind.None, Garble.Scope(page).Kind);
            Assert.False(Garble.PageIsGarbled(page));
        }
    }

    [Fact]
    public void CountLettersAndVowelsAccented()
    {
        Assert.Equal((6, 2), Garble.CountLettersAndVowels("trường"));
        Assert.Equal((6, 2), Garble.CountLettersAndVowels("tru\u031Bo\u031B\u0300ng"));
        Assert.Equal((0, 0), Garble.CountLettersAndVowels("مرحبا こんにちは"));
    }

    [Fact]
    public void CleanPageIsNotGarbled()
    {
        List<TextItem> page = Page(With("Helvetica", HealthyParagraph));
        Assert.Equal(GarbledKind.None, Garble.Scope(page).Kind);
        Assert.False(Garble.PageIsGarbled(page));
    }

    [Theory]
    [InlineData("BadFont")]
    [InlineData(null)]
    public void CipherRowsGarbleTheWholePage(string? font)
        => Assert.Equal(GarbledKind.WholePage, Garble.Scope(Page(With(font, GarbledRows))).Kind);

    [Fact]
    public void MixedFontsFlagOnlyTheBadFont()
    {
        List<TextItem> page = Page([.. With("Helvetica", HealthyParagraph), .. With("BitstreamVeraSans-Roman", GarbledRows)]);
        (int letters, int vowels) = page.Aggregate((0, 0), (acc, it) =>
        {
            (int l, int v) = Garble.CountLettersAndVowels(it.Text);
            return (acc.Item1 + l, acc.Item2 + v);
        });
        Assert.False(Garble.RatioIsGarbled(letters, vowels));
        GarbledScope scope = Garble.Scope(page);
        Assert.Equal(GarbledKind.Fonts, scope.Kind);
        Assert.Equal(["BitstreamVeraSans-Roman"], scope.Fonts);
        Assert.True(Garble.PageIsGarbled(page));
    }

    [Fact]
    public void UnmappedTextGarblesThePage()
    {
        List<TextItem> page = Page(
            ("T3Font", "\uE001\uE002\uE003\uE004\uE005\uE006\uE007\uE008"),
            ("T3Font", "\uE011\uE012\uE013\uE014\uE015\uE016\uE017\uE018"),
            ("T3Font", "\uE021\uE022\uE023\uE024\uE025\uE026\uE027\uE028"),
            ("T3Font", "\uE031\uE032\uE033\uE034\uE035\uE036\uE037\uE038"));
        page.ForEach(i => i.UnicodeMapError = true);
        Assert.Equal(GarbledKind.None, Garble.Scope(page).Kind);
        Assert.Equal(0, OcrPageSelector.NativeTextLength(page));
        Assert.True(Garble.PageIsGarbled(page));

        List<TextItem> healthy = Page([.. With("Helvetica", HealthyParagraph), ("T3Font", "\uE001")]);
        healthy[^1].UnicodeMapError = true;
        Assert.False(Garble.PageIsGarbled(healthy));
    }

    [Fact]
    public void MisreadGlyphsFlagEverySubsetOfTheBaseFont()
    {
        List<TextItem> page = Page(
        [
            .. With("Helvetica", HealthyParagraph),
            ("AAAAAB+Futura", "Tato kter*ch nabíječka je jednou z jin*m"),
            ("AAAAAC+Futura", "nejnověj*ch a b*t"),
            ("AAAAAD+Futura", "Kontakt"),
        ]);
        GarbledScope scope = Garble.Scope(page);
        Assert.Equal(GarbledKind.Fonts, scope.Kind);
        Assert.Contains("AAAAAB+Futura", scope.Fonts);
        Assert.Contains("AAAAAD+Futura", scope.Fonts);
        Assert.DoesNotContain("Helvetica", scope.Fonts);
    }

    [Fact]
    public void CompoundNamesWithOneTailAreNotMisreads()
    {
        List<TextItem> page = Page([.. With("Helvetica", HealthyParagraph), ("STIX-Regular", "HistoAge PhenoAge GrimAge pTau acceleration")]);
        Assert.Equal(GarbledKind.None, Garble.Scope(page).Kind);
    }

    [Theory]
    [InlineData("arXiv arXiv arXiv iPad iPad McRae McRae")]
    [InlineData("the expression\"put out fire\" and emerging\"care in parentheses\"exemplified")]
    [InlineData("70dBc -94dBc 21dBm 25dBm PubTabNet")]
    public void MisreadGlyphsIgnoreNamesQuotesAndUnits(string text)
        => Assert.True(Garble.MisreadGlyphs(text).Select(m => m.Word).Distinct().Count() < 3);

    [Fact]
    public void MisreadGlyphsFindWrongCapitalsAndSymbols()
    {
        var found = Garble.MisreadGlyphs("maGor OecutiMe horiQon va!í");
        Assert.Equal("GMQ!", string.Concat(found.Select(m => m.Ch.ToString())));
        Assert.Equal("magor", found[0].Word);
    }

    [Fact]
    public void SmallLowVowelFontIsIgnored()
    {
        List<TextItem> page = Page([.. With("Helvetica", HealthyParagraph), ("TickerFont", "NYSE NASDAQ FTSE DAX SPX")]);
        Assert.Equal(GarbledKind.None, Garble.Scope(page).Kind);
    }
}
