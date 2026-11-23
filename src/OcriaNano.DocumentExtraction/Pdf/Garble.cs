// Ported from LiteParse crates/liteparse/src/ocr_merge.rs (is_likely_garbled, count_letters_and_vowels, garbled_scope,
// script_intrusions, symbol_intrusions, is_symbol_font, control_chars, script_of, token_is_malformed, malformed_tokens,
// misread_glyphs), Apache-2.0.
using System.Text;

namespace OcriaNano.DocumentExtraction.Pdf;

/// <summary>Which of a page's native text items decode to garbage.</summary>
internal enum GarbledKind
{
    /// <summary>Nothing looks garbled.</summary>
    None,

    /// <summary>The page's text as a whole decodes to garbage.</summary>
    WholePage,

    /// <summary>Specific font groups are garbled while the rest of the page extracts fine.</summary>
    Fonts,
}

/// <summary>The result of <see cref="Garble.Scope"/>: a kind, and for <see cref="GarbledKind.Fonts"/> the flagged font names (null allowed).</summary>
internal sealed record GarbledScope(GarbledKind Kind, IReadOnlySet<string?> Fonts)
{
    public static readonly GarbledScope None = new(GarbledKind.None, new HashSet<string?>());

    public static readonly GarbledScope WholePage = new(GarbledKind.WholePage, new HashSet<string?>());
}

/// <summary>Heuristics for native text whose ToUnicode map is broken (substitution ciphers, legacy fonts, misread glyphs).</summary>
internal static class Garble
{
    internal const int GarbleMinLetters = 30;
    internal const double GarbleMinFontShare = 0.15;
    private const int IntrusionMin = 5;
    private const double IntrusionMinRate = 0.015;
    private const int SymbolMin = 20;
    private const double SymbolMinRate = 0.03;
    private const int ControlMin = 5;
    private const double ControlMinRate = 0.02;
    private const int MalformedMin = 10;
    private const double MalformedMinRate = 0.2;
    private const int MisreadMinWords = 3;
    private const int MisreadMinTails = 2;
    private const string NullFont = "\0<none>";

    private const string AccentedVowels =
        "àáâãäåāăąǎȁȃȧ" + "ạảấầẩẫậắằẳẵặ" + "èéêëēĕėęěȅȇ" + "ẹẻẽếềểễệ" + "ìíîïĩīĭįǐȉȋ" + "ỉị"
        + "òóôõöøōŏőǒȍȏȯ" + "ọỏốồổỗộơớờởỡợ" + "ùúûüũūŭůűųǔǖǘǚǜȕȗ" + "ụủưứừửữự";

    private const string Sym = "$¥©«»@¢£¤¦§¨ª¬®¯¶¸º¼½¾¿";

    /// <summary>Substitution-cipher check for one item: at least 10 letters with fewer than 10% vowels.</summary>
    public static bool IsLikelyGarbled(string text)
    {
        (int letters, int vowels) = CountLettersAndVowels(text);
        return letters >= 10 && vowels * 10 < letters;
    }

    /// <summary>True when a native item can't be trusted: failed Unicode mapping or a garbled vowel ratio.</summary>
    public static bool IsUnusableNative(TextItem item) => item.UnicodeMapError || IsLikelyGarbled(item.Text);

    /// <summary>ASCII letters plus precomposed accented vowels, and the vowels among them.</summary>
    public static (int Letters, int Vowels) CountLettersAndVowels(string text)
    {
        int letters = 0, vowels = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (r.IsAscii)
            {
                char c = (char)r.Value;
                if (char.IsAsciiLetter(c))
                {
                    letters++;
                    if ("aeiou".Contains(char.ToLowerInvariant(c), StringComparison.Ordinal))
                    {
                        vowels++;
                    }
                }
            }
            else if (AccentedVowels.Contains(Rune.ToLowerInvariant(r).ToString(), StringComparison.Ordinal))
            {
                letters++;
                vowels++;
            }
        }

        return (letters, vowels);
    }

    internal static bool RatioIsGarbled(int letters, int vowels) => letters >= GarbleMinLetters && vowels * 5 < letters;

    private static bool IntrusionsAreGarbled(int script, int intrusions)
        => intrusions >= IntrusionMin && intrusions >= script * IntrusionMinRate;

    private static bool SymbolsAreGarbled(int letters, int intrusions)
        => intrusions >= SymbolMin && intrusions >= letters * SymbolMinRate;

    private static bool ControlsAreGarbled(int ctrl, int chars) => ctrl >= ControlMin && ctrl >= chars * ControlMinRate;

    private static bool TokensAreGarbled(int judged, int malformed)
        => malformed >= MalformedMin && malformed >= judged * MalformedMinRate;

    /// <summary>True when the page's native text is garbled anywhere, or a real share of it failed Unicode mapping.</summary>
    public static bool PageIsGarbled(IReadOnlyList<TextItem> items) => Scope(items).Kind != GarbledKind.None || HasUnmappedText(items);

    private static bool HasUnmappedText(IReadOnlyList<TextItem> items)
    {
        int unmapped = 0, total = 0;
        foreach (TextItem it in items)
        {
            int n = it.Text.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r));
            total += n;
            if (it.UnicodeMapError)
            {
                unmapped += n;
            }
        }

        return unmapped >= GarbleMinLetters && unmapped >= total * GarbleMinFontShare;
    }

    /// <summary>Which native items are untrustworthy, judged page-wide first and then per font group.</summary>
    public static GarbledScope Scope(IReadOnlyList<TextItem> items)
    {
        int totalLetters = 0, totalVowels = 0, totalScript = 0, totalIntrusions = 0, totalSymLetters = 0, totalSym = 0;
        int totalCtrl = 0, totalChars = 0, totalJudged = 0, totalMalformed = 0;
        Dictionary<string, (int A, int B)> perFont = [], perFontScript = [], perFontSymbol = [], perFontCtrl = [], perFontTok = [];
        Dictionary<string, Dictionary<Rune, (HashSet<string> Words, HashSet<string> Tails)>> perBaseMisread = [];

        static void Add(Dictionary<string, (int A, int B)> d, string key, int a, int b)
        {
            (int x, int y) = d.GetValueOrDefault(key);
            d[key] = (x + a, y + b);
        }

        foreach (TextItem it in items)
        {
            string key = it.FontName ?? NullFont;
            if (!IsSymbolFont(it.FontName))
            {
                string baseKey = it.FontName is null ? NullFont : BaseFontName(it.FontName);
                if (!perBaseMisread.TryGetValue(baseKey, out var byChar))
                {
                    perBaseMisread[baseKey] = byChar = [];
                }

                foreach ((Rune ch, string word, string tail) in MisreadGlyphs(it.Text))
                {
                    if (!byChar.TryGetValue(ch, out var e))
                    {
                        byChar[ch] = e = ([], []);
                    }

                    e.Words.Add(word);
                    e.Tails.Add(tail);
                }

                (int c, int n) = ControlChars(it.Text);
                totalCtrl += c;
                totalChars += n;
                Add(perFontCtrl, key, c, n);
                (int j, int m) = MalformedTokens(it.Text);
                totalJudged += j;
                totalMalformed += m;
                Add(perFontTok, key, j, m);
            }

            (int l, int v) = CountLettersAndVowels(it.Text);
            totalLetters += l;
            totalVowels += v;
            Add(perFont, key, l, v);
            (int sc, int intr) = ScriptIntrusions(it.Text);
            totalScript += sc;
            totalIntrusions += intr;
            Add(perFontScript, key, sc, intr);
            (int lt, int sy) = SymbolIntrusions(it.Text);
            totalSymLetters += lt;
            totalSym += sy;
            Add(perFontSymbol, key, lt, sy);
        }

        if (RatioIsGarbled(totalLetters, totalVowels)
            || IntrusionsAreGarbled(totalScript, totalIntrusions)
            || SymbolsAreGarbled(totalSymLetters, totalSym)
            || ControlsAreGarbled(totalCtrl, totalChars)
            || TokensAreGarbled(totalJudged, totalMalformed))
        {
            return GarbledScope.WholePage;
        }

        int minShare = (int)Math.Ceiling(totalLetters * GarbleMinFontShare);
        HashSet<string> flagged = [];
        flagged.UnionWith(perFont.Where(p => p.Value.A >= minShare && RatioIsGarbled(p.Value.A, p.Value.B)).Select(p => p.Key));
        flagged.UnionWith(perFontScript.Where(p => IntrusionsAreGarbled(p.Value.A, p.Value.B)).Select(p => p.Key));
        flagged.UnionWith(perFontSymbol.Where(p => SymbolsAreGarbled(p.Value.A, p.Value.B)).Select(p => p.Key));
        flagged.UnionWith(perFontCtrl.Where(p => ControlsAreGarbled(p.Value.A, p.Value.B)).Select(p => p.Key));
        flagged.UnionWith(perFontTok.Where(p => TokensAreGarbled(p.Value.A, p.Value.B)).Select(p => p.Key));

        // A misread glyph condemns every subset of its base font on the page.
        HashSet<string> misreadBases = [.. perBaseMisread
            .Where(p => p.Value.Values.Any(e => e.Words.Count >= MisreadMinWords && e.Tails.Count >= MisreadMinTails))
            .Select(p => p.Key)];
        if (misreadBases.Count > 0)
        {
            foreach (TextItem it in items)
            {
                if (misreadBases.Contains(it.FontName is null ? NullFont : BaseFontName(it.FontName)))
                {
                    flagged.Add(it.FontName ?? NullFont);
                }
            }
        }

        if (flagged.Count == 0)
        {
            return GarbledScope.None;
        }

        HashSet<string?> fonts = [.. flagged.Select(f => f == NullFont ? null : f)];
        return new GarbledScope(GarbledKind.Fonts, fonts);
    }

    /// <summary>True for an item whose font group <paramref name="scope"/> flags.</summary>
    public static bool InScope(GarbledScope scope, TextItem item) => scope.Kind switch
    {
        GarbledKind.WholePage => true,
        GarbledKind.Fonts => scope.Fonts.Contains(item.FontName),
        _ => false,
    };

    internal static bool IsComplexScript(Rune r) => r.Value switch
    {
        >= 0x0590 and <= 0x05FF or >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF
            or >= 0x0900 and <= 0x0DFF or >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF
            or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF => true,
        _ => false,
    };

    /// <summary>(complex-script characters, printable ASCII characters wedged between two of them).</summary>
    internal static (int Script, int Intrusions) ScriptIntrusions(string text)
    {
        Rune[] cs = Runes(text);
        int script = cs.Count(IsComplexScript);
        int intrusions = 0;
        for (int i = 1; i < cs.Length - 1; i++)
        {
            if (cs[i].Value is >= 0x21 and <= 0x7E && IsComplexScript(cs[i - 1]) && IsComplexScript(cs[i + 1]))
            {
                intrusions++;
            }
        }

        return (script, intrusions);
    }

    /// <summary>(letters, symbols such as <c>$ ¥ © « @ § ¶ ®</c> wedged between two letters).</summary>
    internal static (int Letters, int Intrusions) SymbolIntrusions(string text)
    {
        Rune[] cs = Runes(text);
        int letters = cs.Count(IsAlphabetic);
        int n = 0;
        for (int i = 1; i < cs.Length - 1; i++)
        {
            if (Sym.Contains(cs[i].ToString(), StringComparison.Ordinal) && IsAlphabetic(cs[i - 1]) && IsAlphabetic(cs[i + 1]))
            {
                n++;
            }
        }

        return (letters, n);
    }

    /// <summary>Dingbat, symbol and TeX math fonts, whose glyphs map to control and private codes by design.</summary>
    public static bool IsSymbolFont(string? name)
    {
        if (name is null)
        {
            return false;
        }

        string n = name.ToLowerInvariant();
        int plus = n.IndexOf('+', StringComparison.Ordinal);
        string b = plus >= 0 ? n[(plus + 1)..] : n;
        string[] contains = ["wingding", "webding", "dingbat", "symbol", "zapf", "math", "sym", "greekwith"];
        string[] prefixes = ["cmex", "cmsy", "cmmi", "msam", "msbm", "rtxm", "rtxs", "txex", "txsy", "xyatip", "xydash", "eufm", "esint", "stmary", "wasy"];
        return contains.Any(s => b.Contains(s, StringComparison.Ordinal)) || prefixes.Any(p => b.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>(control characters other than tab / newline / carriage return, visible characters including them).</summary>
    internal static (int Ctrl, int Chars) ControlChars(string text)
    {
        int ctrl = 0, visible = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (Rune.IsControl(r) && r.Value is not ('\t' or '\n' or '\r'))
            {
                ctrl++;
            }
            else if (!Rune.IsWhiteSpace(r))
            {
                visible++;
            }
        }

        return (ctrl, visible + ctrl);
    }

    /// <summary>Coarse script id: 0 Latin, 1 CJK, a per-script id for scripts that can be told apart, 2 for the rest.</summary>
    internal static int ScriptOf(Rune r)
    {
        int o = r.Value;
        return o switch
        {
            <= 0x24F or >= 0x1E00 and <= 0x1EFF or >= 0x2C60 and <= 0x2C7F or >= 0xA720 and <= 0xA7FF => 0,
            >= 0x1100 and <= 0x11FF or >= 0x3000 and <= 0x30FF or >= 0x3400 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF
                or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF => 1,
            >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF or >= 0x2C80 and <= 0x2CFF => 0x370,
            >= 0x0400 and <= 0x052F or >= 0x1C80 and <= 0x1C8F or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F => 0x400,
            >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F => 0x590,
            >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0860 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF
                or >= 0xFE70 and <= 0xFEFF => 0x600,
            >= 0x1200 and <= 0x139F or >= 0x2D80 and <= 0x2DDF or >= 0xAB00 and <= 0xAB2F => 0x1200,
            >= 0x10A0 and <= 0x10FF or >= 0x1C90 and <= 0x1CBF or >= 0x2D00 and <= 0x2D2F => 0x10A0,
            >= 0x1780 and <= 0x17FF or >= 0x19E0 and <= 0x19FF => 0x1780,
            >= 0x1000 and <= 0x109F or >= 0xA9E0 and <= 0xA9FF or >= 0xAA60 and <= 0xAA7F => 0x1000,
            >= 0x0530 and <= 0x058F or >= 0xFB13 and <= 0xFB17 => 0x530,
            >= 0x0700 and <= 0x074F => 0x700,
            >= 0x0900 and <= 0x0DFF => o & ~0x7F,
            >= 0x0E00 and <= 0x0E7F => 0xE00,
            >= 0x0E80 and <= 0x0EFF => 0xE80,
            >= 0x0F00 and <= 0x0FFF => 0xF00,
            _ => 2,
        };
    }

    /// <summary>A token whose shape no correctly decoded text has; null when the token is too short to judge.</summary>
    internal static bool? TokenIsMalformed(string tok)
    {
        if (tok.Contains('$', StringComparison.Ordinal) || tok.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        Rune[] cs = Trim(Runes(tok), r => !IsAlphanumeric(r));
        if (cs.Length < 3 || cs.Count(IsAlphabetic) < 2)
        {
            return null;
        }

        if (!cs.Any(r => r.Value is '_' or '.' or '/' or '@'))
        {
            int flips = 0;
            bool shortRun = false;
            for (int i = 1; i < cs.Length - 1; i++)
            {
                if (Rune.IsLower(cs[i - 1]) && Rune.IsUpper(cs[i]) && Rune.IsLower(cs[i + 1]))
                {
                    flips++;
                    int run = cs.Skip(i + 1).TakeWhile(Rune.IsLower).Count();
                    shortRun |= run < 3;
                }
            }

            if (flips >= 2 || shortRun)
            {
                return true;
            }
        }

        for (int i = 1; i < cs.Length - 1; i++)
        {
            if (cs[i].Value is ';' or '=' && IsAlphabetic(cs[i - 1]) && IsAlphabetic(cs[i + 1]))
            {
                return true;
            }
        }

        static bool Other(Rune c) => IsAlphabetic(c) && c.Value is not (>= 0x300 and <= 0x36F) && ScriptOf(c) > 1;
        static bool Accented(Rune c) => IsAlphabetic(c) && c.Value is >= 0xC0 and <= 0x24F;
        int? seen = null;
        foreach (Rune c in cs)
        {
            if (Other(c))
            {
                int s = ScriptOf(c);
                if (seen is int p && p != s)
                {
                    return true;
                }

                seen = s;
            }
        }

        for (int i = 0; i + 1 < cs.Length; i++)
        {
            if ((Accented(cs[i]) && Other(cs[i + 1])) || (Other(cs[i]) && Accented(cs[i + 1])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>(judged tokens, malformed tokens) in <paramref name="text"/>, control characters read as spaces.</summary>
    internal static (int Judged, int Malformed) MalformedTokens(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (Rune r in text.EnumerateRunes())
        {
            sb.Append(Rune.IsControl(r) ? " " : r.ToString());
        }

        int judged = 0, malformed = 0;
        foreach (string tok in SplitWhitespace(sb.ToString()))
        {
            if (TokenIsMalformed(tok) is bool m)
            {
                judged++;
                malformed += m ? 1 : 0;
            }
        }

        return (judged, malformed);
    }

    /// <summary>A font's name without its subset tag ("AAAAAB+Futura" → "Futura").</summary>
    internal static string BaseFontName(string name)
    {
        int plus = name.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? name[(plus + 1)..] : name;
    }

    /// <summary>Characters a font map put where a letter belongs, each with its word (letters, lowercased) and the tail it opens.</summary>
    internal static List<(Rune Ch, string Word, string Tail)> MisreadGlyphs(string text)
    {
        const string TrimChars = ".,;:()[]{}'\"!?«»“”‘’";
        List<(Rune, string, string)> output = [];
        foreach (string tok in SplitWhitespace(text))
        {
            Rune[] cs = Trim(Runes(tok), r => TrimChars.Contains(r.ToString(), StringComparison.Ordinal));
            string t = string.Concat(cs.Select(r => r.ToString()));
            if (t.StartsWith("http", StringComparison.Ordinal) || cs.Any(r => r.Value is '_' or '@' or '\\' or '$' or '/'))
            {
                continue;
            }

            if (cs.Length < 3)
            {
                continue;
            }

            string Word() => string.Concat(cs.Where(IsAlphabetic).Select(r => Rune.ToLowerInvariant(r).ToString()));
            static bool Alphabetic(Rune c) => IsAlphabetic(c) && ScriptOf(c) is 0 or 0x370 or 0x400;
            for (int i = 1; i < cs.Length - 1; i++)
            {
                if (cs[i].Value is '!' or '%' or '*' && Alphabetic(cs[i - 1]) && Alphabetic(cs[i + 1]))
                {
                    output.Add((cs[i], Word(), Word()));
                }
            }

            if (cs.Any(r => r.Value is >= '0' and <= '9'))
            {
                continue;
            }

            for (int i = 2; i < cs.Length - 1; i++)
            {
                if (Rune.IsLower(cs[i - 1]) && Rune.IsUpper(cs[i]) && Rune.IsLower(cs[i + 1]))
                {
                    int tail = cs.Skip(i + 1).TakeWhile(Rune.IsLower).Count();
                    if (i + 1 + tail == cs.Length && tail < 3)
                    {
                        output.Add((cs[i], Word(), string.Concat(cs.Skip(i).Select(r => r.ToString()))));
                    }
                }
            }
        }

        return output;
    }

    /// <summary>Approximates Rust's <c>char::is_alphabetic</c>: letters plus combining marks (most carry Other_Alphabetic).</summary>
    internal static bool IsAlphabetic(Rune r)
    {
        if (Rune.IsLetter(r))
        {
            return true;
        }

        System.Globalization.UnicodeCategory cat = Rune.GetUnicodeCategory(r);
        return cat is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.LetterNumber;
    }

    private static bool IsAlphanumeric(Rune r) => IsAlphabetic(r) || Rune.IsNumber(r);

    private static Rune[] Runes(string s) => [.. s.EnumerateRunes()];

    private static Rune[] Trim(Rune[] cs, Func<Rune, bool> drop)
    {
        int a = 0, b = cs.Length;
        while (a < b && drop(cs[a]))
        {
            a++;
        }

        while (b > a && drop(cs[b - 1]))
        {
            b--;
        }

        return cs[a..b];
    }

    internal static IEnumerable<string> SplitWhitespace(string s)
    {
        StringBuilder cur = new();
        foreach (Rune r in s.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r))
            {
                if (cur.Length > 0)
                {
                    yield return cur.ToString();
                    cur.Clear();
                }
            }
            else
            {
                cur.Append(r.ToString());
            }
        }

        if (cur.Length > 0)
        {
            yield return cur.ToString();
        }
    }
}
