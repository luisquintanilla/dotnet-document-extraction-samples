// Ported from LiteParse crates/liteparse/src/ocr/ocria/rec.rs (visual_to_logical, via unicode-bidi), Apache-2.0.
using System.Globalization;
using System.Text;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// A compact Unicode Bidirectional Algorithm for one paragraph with a right-to-left base level (rules W1-W7, N1-N2,
/// I1-I2, L1-L2). No explicit embeddings or isolates, no bracket pairing (N0) and no mirroring, matching how the
/// reference reorders recognizer output.
/// </summary>
internal static class Bidi
{
    private enum T { L, R, AL, EN, ES, ET, AN, CS, NSM, WS, ON }

    public static bool IsRtlChar(Rune r) => r.Value is (>= 0x0590 and <= 0x08ff) or (>= 0xfb1d and <= 0xfeff);

    private static T Classify(Rune r)
    {
        int v = r.Value;
        UnicodeCategory cat = Rune.GetUnicodeCategory(r);
        if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark)
        {
            return T.NSM;
        }

        if (v is >= '0' and <= '9' or >= 0x06f0 and <= 0x06f9)
        {
            return T.EN;
        }

        if (v is (>= 0x0660 and <= 0x0669) or 0x066b or 0x066c)
        {
            return T.AN;
        }

        if (v is '+' or '-')
        {
            return T.ES;
        }

        if (v is '#' or '$' or '%' or (>= 0x00a2 and <= 0x00a5) or 0x00b0 or 0x00b1 or 0x066a or 0x20a0 or 0x20ac)
        {
            return T.ET;
        }

        if (v is ',' or '.' or '/' or ':' or 0x00a0 or 0x060c)
        {
            return T.CS;
        }

        if (v is >= 0x0590 and <= 0x05ff or >= 0x07c0 and <= 0x085f or >= 0xfb1d and <= 0xfb4f or >= 0x10800 and <= 0x10fff)
        {
            return T.R;
        }

        if (v is >= 0x0600 and <= 0x07bf or >= 0x0860 and <= 0x08ff or >= 0xfb50 and <= 0xfdff or >= 0xfe70 and <= 0xfeff)
        {
            return cat is UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter ? T.AL : T.ON;
        }

        if (Rune.IsWhiteSpace(r))
        {
            return T.WS;
        }

        return Rune.IsLetter(r) || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.LetterNumber ? T.L : T.ON;
    }

    private static bool IsNeutral(T t) => t is T.WS or T.ON;

    /// <summary>Reorders a visually ordered string to logical order for a right-to-left paragraph.</summary>
    public static string VisualToLogical(string s)
    {
        Rune[] runes = [.. s.EnumerateRunes()];
        int n = runes.Length;
        if (n == 0)
        {
            return s;
        }

        T[] orig = new T[n];
        for (int i = 0; i < n; i++)
        {
            orig[i] = Classify(runes[i]);
        }

        T[] t = (T[])orig.Clone();

        // W1
        T prev = T.R;
        for (int i = 0; i < n; i++)
        {
            if (t[i] == T.NSM)
            {
                t[i] = prev;
            }

            prev = t[i];
        }

        // W2
        T lastStrong = T.R;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is T.L or T.R or T.AL)
            {
                lastStrong = t[i];
            }
            else if (t[i] == T.EN && lastStrong == T.AL)
            {
                t[i] = T.AN;
            }
        }

        // W3
        for (int i = 0; i < n; i++)
        {
            if (t[i] == T.AL)
            {
                t[i] = T.R;
            }
        }

        // W4
        for (int i = 1; i + 1 < n; i++)
        {
            if (t[i] == T.ES && t[i - 1] == T.EN && t[i + 1] == T.EN)
            {
                t[i] = T.EN;
            }
            else if (t[i] == T.CS && t[i - 1] == t[i + 1] && t[i - 1] is T.EN or T.AN)
            {
                t[i] = t[i - 1];
            }
        }

        // W5
        for (int i = 0; i < n;)
        {
            if (t[i] != T.ET)
            {
                i++;
                continue;
            }

            int j = i;
            while (j < n && t[j] == T.ET)
            {
                j++;
            }

            if ((i > 0 && t[i - 1] == T.EN) || (j < n && t[j] == T.EN))
            {
                for (int k = i; k < j; k++)
                {
                    t[k] = T.EN;
                }
            }

            i = j;
        }

        // W6
        for (int i = 0; i < n; i++)
        {
            if (t[i] is T.ES or T.ET or T.CS)
            {
                t[i] = T.ON;
            }
        }

        // W7
        lastStrong = T.R;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is T.L or T.R)
            {
                lastStrong = t[i];
            }
            else if (t[i] == T.EN && lastStrong == T.L)
            {
                t[i] = T.L;
            }
        }

        // N1, N2 (sos = eos = R, embedding direction R)
        for (int i = 0; i < n;)
        {
            if (!IsNeutral(t[i]))
            {
                i++;
                continue;
            }

            int j = i;
            while (j < n && IsNeutral(t[j]))
            {
                j++;
            }

            T before = i == 0 ? T.R : Strong(t[i - 1]);
            T after = j == n ? T.R : Strong(t[j]);
            T resolved = before == after ? before : T.R;
            for (int k = i; k < j; k++)
            {
                t[k] = resolved;
            }

            i = j;
        }

        // I1, I2 from base level 1
        int[] level = new int[n];
        for (int i = 0; i < n; i++)
        {
            level[i] = t[i] == T.R ? 1 : 2;
        }

        // L1: trailing whitespace goes back to the paragraph level
        for (int i = n - 1; i >= 0 && orig[i] == T.WS; i--)
        {
            level[i] = 1;
        }

        // L2
        int[] order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        for (int lv = 2; lv >= 1; lv--)
        {
            for (int i = 0; i < n;)
            {
                if (level[order[i]] < lv)
                {
                    i++;
                    continue;
                }

                int j = i;
                while (j < n && level[order[j]] >= lv)
                {
                    j++;
                }

                Array.Reverse(order, i, j - i);
                i = j;
            }
        }

        StringBuilder sb = new(s.Length);
        foreach (int i in order)
        {
            sb.Append(runes[i].ToString());
        }

        return sb.ToString();
    }

    private static T Strong(T t) => t is T.EN or T.AN ? T.R : t;
}
