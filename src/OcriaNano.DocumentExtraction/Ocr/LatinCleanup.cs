// Ported from LiteParse crates/liteparse/src/ocr/ocria/page.rs, Apache-2.0.
using System.Globalization;
using System.Text;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>Cleans readings of other scripts' lookalike characters (<c>page::clean_latin</c>).</summary>
internal static class LatinCleanup
{
    // Cyrillic / Greek lookalikes of Latin letters (server._HOMO).
    private const string HomoFrom = "аеорсухкіјѕԁԛԝАВЕКМНОРСТХІЈЅΑΒΕΖΗΙΚΜΝΟΡΤΥΧοαν";
    private const string HomoTo = "aeopcyxkijsdqwABEKMHOPCTXIJSABEZHIKMNOPTYXoav";

    // Lookalikes from the other scripts a Latin line picks up. The two strings differ in length; pairs stop at the shorter.
    private const string HomoExtraFrom = "ՍսՕօոհցզաԼՏนทรอบ๐ບນຣ໐၀ဝ";
    private const string HomoExtraTo = "UuOonhgqwLSunsououusooo";

    // First code point of each run of decimal digits the recognizers can write besides ASCII.
    private static readonly int[] s_digitZeros =
    [
        0x0660, 0x06f0, 0x0966, 0x09e6, 0x0a66, 0x0ae6, 0x0b66, 0x0be6, 0x0c66, 0x0ce6, 0x0d66, 0x0de6,
        0x0e50, 0x0ed0, 0x0f20, 0x1040, 0x1090, 0x17e0,
    ];

    // Latin and Greek lookalikes of Cyrillic letters.
    private const string CyrFrom = "aceopxyiABCEHKMOPTXYIΑΒΓΕΗΚΜΟΠΡΤΧαο";
    private const string CyrTo = "асеорхуіАВСЕНКМОРТХУІАВГЕНКМОПРТХао";

    /// <summary>
    /// Approximates Unicode's Alphabetic property (Rust <c>char::is_alphabetic</c>): letters, letter numbers,
    /// spacing marks, and the non-spacing vowel signs of the Indic and South-East Asian scripts (not tone marks or viramas).
    /// </summary>
    internal static bool IsAlphabetic(Rune r)
    {
        switch (Rune.GetUnicodeCategory(r))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.SpacingCombiningMark:
                return true;
            case UnicodeCategory.NonSpacingMark:
                int v = r.Value;
                if (v is (>= 0x0e48 and <= 0x0e4e) or (>= 0x0ec8 and <= 0x0ecd) or 0x0e47)
                {
                    return v == 0x0e4d;
                }

                return (v is >= 0x0900 and <= 0x0dff && !IsVirama(v)) || v is (>= 0x0e00 and <= 0x0eff) or (>= 0x1000 and <= 0x109f) or (>= 0x1780 and <= 0x17ff);
            default:
                return false;
        }
    }

    private static bool IsVirama(int v) => v is 0x094d or 0x09cd or 0x0a4d or 0x0acd or 0x0b4d or 0x0bcd or 0x0c4d or 0x0ccd or 0x0d4d or 0x0dca
        or 0x093c or 0x09bc or 0x0a3c or 0x0abc or 0x0b3c or 0x0cbc;

    private static bool IsLatinLetter(Rune r)
        => (r.Value < 128 && char.IsAsciiLetter((char)r.Value)) || (IsAlphabetic(r) && r.Value is >= 0x00c0 and <= 0x024f);

    /// <summary>Latin letters in <paramref name="text"/> (ASCII and Latin-1 / Extended-A/B; not Greek or Cyrillic).</summary>
    public static int LatinLetters(string text)
    {
        int n = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (IsLatinLetter(r))
            {
                n++;
            }
        }

        return n;
    }

    private static int OtherLetters(string text)
    {
        int n = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (IsAlphabetic(r) && !IsLatinLetter(r))
            {
                n++;
            }
        }

        return n;
    }

    private static int CountIn(string text, int lo, int hi)
    {
        int n = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (r.Value >= lo && r.Value <= hi)
            {
                n++;
            }
        }

        return n;
    }

    private static Rune? Pair(string from, string to, Rune c)
    {
        int n = Math.Min(from.Length, to.Length);
        for (int i = 0; i < n; i++)
        {
            if (from[i] == c.Value)
            {
                return new Rune(to[i]);
            }
        }

        return null;
    }

    private static Rune? Homoglyph(Rune c) => Pair(HomoFrom, HomoTo, c) ?? Pair(HomoExtraFrom, HomoExtraTo, c);

    private static Rune? LatinLookalike(Rune c, bool upper)
        => Homoglyph(c) ?? c.Value switch
        {
            'т' => new Rune(upper ? 'T' : 't'),
            'н' => new Rune(upper ? 'H' : 'h'),
            'м' => new Rune(upper ? 'M' : 'm'),
            'п' or 'л' => new Rune('n'),
            'г' => new Rune('r'),
            _ => null,
        };

    private static bool InGreekOrCyrillic(Rune c) => c.Value is >= 0x0370 and <= 0x04ff;

    private static bool IsCyrillic(Rune c) => c.Value is >= 0x0400 and <= 0x04ff;

    /// <summary>
    /// In a line that is mostly Latin, inside a word with at least as many Latin letters as other letters, lookalikes
    /// become the Latin letter, and other scripts' digits become ASCII digits. Lines mostly in another script are left alone.
    /// </summary>
    public static string CleanLatin(string text)
    {
        int lat = LatinLetters(text);
        int oth = OtherLetters(text);
        bool latinLine = lat > 0 && lat >= 2 * oth;
        bool cyrLine = CountIn(text, 0x0400, 0x04ff) > lat;
        StringBuilder sb = new(text.Length);
        string[] words = SplitWhitespace(text);
        for (int k = 0; k < words.Length; k++)
        {
            string word = words[k];
            if (k > 0)
            {
                sb.Append(' ');
            }

            int wl = LatinLetters(word);
            int wo = OtherLetters(word);
            int up = 0, low = 0;
            foreach (Rune c in word.EnumerateRunes())
            {
                if (Rune.IsUpper(c))
                {
                    up++;
                }

                if (Rune.IsLower(c))
                {
                    low++;
                }
            }

            bool upper = up > low;
            bool allLookalike = wo > 0;
            if (allLookalike)
            {
                foreach (Rune c in word.EnumerateRunes())
                {
                    if (!(!IsAlphabetic(c) || IsLatinLetter(c) || (LatinLookalike(c, upper) is not null && !(c.Value is >= 0x0370 and <= 0x03ff))))
                    {
                        allLookalike = false;
                        break;
                    }
                }
            }

            int cg = CountIn(word, 0x0370, 0x04ff);
            bool foldCg = cg > 0 && wl >= cg;
            bool fold = latinLine && wo > 0 && (wl >= wo || allLookalike);
            int cyr = CountIn(word, 0x0400, 0x04ff);
            bool lookalikesOnly = true;
            foreach (Rune c in word.EnumerateRunes())
            {
                if (IsAlphabetic(c) && !IsCyrillic(c) && !(c.Value < 0x10000 && CyrFrom.Contains((char)c.Value)))
                {
                    lookalikesOnly = false;
                    break;
                }
            }

            bool toCyr = (cyr >= 2 && cyr > wl + (cg - cyr)) || (cyr >= 1 && cyrLine && lookalikesOnly);
            foreach (Rune orig in word.EnumerateRunes())
            {
                Rune c = orig;
                if (toCyr)
                {
                    c = Pair(CyrFrom, CyrTo, c) ?? c;
                }
                else if (fold || (foldCg && InGreekOrCyrillic(c)))
                {
                    c = LatinLookalike(c, upper) ?? c;
                }

                int u = c.Value;
                int zero = -1;
                foreach (int z in s_digitZeros)
                {
                    if (u >= z && u < z + 10)
                    {
                        zero = z;
                        break;
                    }
                }

                if (zero >= 0 && latinLine)
                {
                    sb.Append((char)('0' + (u - zero)));
                }
                else
                {
                    sb.Append(c.ToString());
                }
            }
        }

        return sb.ToString();
    }

    // Rust str::split(char::is_whitespace): consecutive separators yield empty words.
    private static string[] SplitWhitespace(string text)
    {
        List<string> words = [];
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                words.Add(text[start..i]);
                start = i + 1;
            }
        }

        words.Add(text[start..]);
        return [.. words];
    }
}
