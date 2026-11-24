// Ported from LiteParse crates/liteparse/src/table_model/features.rs (unit_features, unit_rule_features, pair_rule_features), Apache-2.0.
using System.Text;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>A ruling line in page points: for horizontal rules <c>P</c> is y and the extent is x0..x1; for vertical rules <c>P</c> is x and the extent is y0..y1.</summary>
internal readonly record struct RuleSegment(float P, float A0, float A1);

/// <summary>Ruling lines in page points.</summary>
internal sealed class Rules
{
    public List<RuleSegment> H { get; } = [];

    public List<RuleSegment> V { get; } = [];
}

internal static partial class UnitFeatures
{
    public const int NFourier = 8;

    private static float MedianF32(float[] v)
    {
        float[] s = [.. v];
        Array.Sort(s);
        int n = s.Length;
        if (n == 0)
        {
            return 0f;
        }

        return n % 2 == 1 ? s[n / 2] : (s[(n / 2) - 1] + s[n / 2]) / 2f;
    }

    /// <summary>tn_core._NUM: <c>^[\s(\[$€£¥+\-–−±&lt;&gt;≤≥~]*[\d.,]+[%)\]*†‡§a-z]*$</c> (case-insensitive).</summary>
    internal static bool IsNumericText(string t)
    {
        static bool Lead(char c) => char.IsWhiteSpace(c) || "([$€£¥+-–−±<>≤≥~".Contains(c);
        static bool Body(char c) => c is >= '0' and <= '9' or '.' or ',';
        static bool Tail(char c) => "%)]*†‡§".Contains(c) || c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        int i = 0;
        while (i < t.Length && Lead(t[i]) && !Body(t[i]))
        {
            i++;
        }

        int start = i;
        while (i < t.Length && Body(t[i]))
        {
            i++;
        }

        if (i == start)
        {
            return false;
        }

        for (; i < t.Length; i++)
        {
            if (!Tail(t[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigit(Rune c) =>
        c.Value is >= '0' and <= '9' or '²' or '³' or '¹' or (>= '⁰' and <= '⁹') or (>= '₀' and <= '₉');

    /// <summary>[N, 4]: a horizontal rule just above / below the unit, a vertical rule just left / right.</summary>
    public static float[][] UnitRuleFeatures(float[][] boxes, Rules rules, float[] h)
    {
        float[][] output = new float[boxes.Length][];
        for (int i = 0; i < boxes.Length; i++)
        {
            output[i] = new float[4];
            float[] b = boxes[i];
            float hh = MathF.Max(h[i], 0.5f);
            foreach (RuleSegment r in rules.H)
            {
                bool ov = MathF.Min(b[2], r.A1) - MathF.Max(b[0], r.A0) > 0f;
                if (!ov)
                {
                    continue;
                }

                float above = b[1] - r.P;
                float below = r.P - b[3];
                if (above >= -0.3f * hh && above < 1.5f * hh)
                {
                    output[i][0] = 1f;
                }

                if (below >= -0.3f * hh && below < 1.5f * hh)
                {
                    output[i][1] = 1f;
                }
            }

            foreach (RuleSegment r in rules.V)
            {
                bool ov = MathF.Min(b[3], r.A1) - MathF.Max(b[1], r.A0) > 0f;
                if (!ov)
                {
                    continue;
                }

                float left = b[0] - r.P;
                float right = r.P - b[2];
                if (left >= -0.3f * hh && left < 2f * hh)
                {
                    output[i][2] = 1f;
                }

                if (right >= -0.3f * hh && right < 2f * hh)
                {
                    output[i][3] = 1f;
                }
            }
        }

        return output;
    }

    /// <summary>[N, N, 2]: rules strictly between two units (spanning both), counts capped at 3, scaled.</summary>
    public static float[] PairRuleFeatures(float[][] boxes, Rules rules)
    {
        int n = boxes.Length;
        float[] output = new float[n * n * 2];
        const float cap = 3f;
        float[] cy = boxes.Select(b => (b[1] + b[3]) / 2f).ToArray();
        float[] cx = boxes.Select(b => (b[0] + b[2]) / 2f).ToArray();
        bool[][] hcov = boxes.Select(b => rules.H.Select(r => r.A0 <= b[2] && r.A1 >= b[0]).ToArray()).ToArray();
        bool[][] vcov = boxes.Select(b => rules.V.Select(r => r.A0 <= b[3] && r.A1 >= b[1]).ToArray()).ToArray();
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (rules.H.Count > 0)
                {
                    float lo = MathF.Min(cy[i], cy[j]), hi = MathF.Max(cy[i], cy[j]);
                    int c = 0;
                    for (int r = 0; r < rules.H.Count; r++)
                    {
                        float p = rules.H[r].P;
                        if (p > lo && p < hi && hcov[i][r] && hcov[j][r])
                        {
                            c++;
                        }
                    }

                    output[((i * n) + j) * 2] = MathF.Min(c, cap) / cap;
                }

                if (rules.V.Count > 0)
                {
                    float lo = MathF.Min(cx[i], cx[j]), hi = MathF.Max(cx[i], cx[j]);
                    int c = 0;
                    for (int r = 0; r < rules.V.Count; r++)
                    {
                        float p = rules.V[r].P;
                        if (p > lo && p < hi && vcov[i][r] && vcov[j][r])
                        {
                            c++;
                        }
                    }

                    output[(((i * n) + j) * 2) + 1] = MathF.Min(c, cap) / cap;
                }
            }
        }

        return output;
    }

    /// <summary>
    /// [N, 68] row-major (tn_core.unit_features with rules; <paramref name="rules"/> null = unknown). The 8 projection
    /// dimensions stay zero and the block / leaf ids are -1 (LiteParse's <c>proj: None</c> path).
    /// </summary>
    public static float[] Compute(IReadOnlyList<Unit> units, float wPg, float hPg, Rules? rules)
    {
        int n = units.Count;
        float[] f = new float[n * Dim];
        if (n == 0)
        {
            return f;
        }

        float[][] b = units.Select(u => new[] { (float)u.Bx.X0, (float)u.Bx.Y0, (float)u.Bx.X1, (float)u.Bx.Y1 }).ToArray();
        float[] h = units.Select(u => MathF.Max((float)u.H, 0.5f)).ToArray();
        float hm = MedianF32(h);
        float[] mid = b.Select(x => (x[1] + x[3]) / 2f).ToArray();
        float[][]? ruleF = rules is null
            ? null
            : UnitRuleFeatures(
                units.Select(u => new[] { (float)u.PBox.X0, (float)u.PBox.Y0, (float)u.PBox.X1, (float)u.PBox.Y1 }).ToArray(),
                rules,
                units.Select(u => (float)u.H).ToArray());
        for (int i = 0; i < n; i++)
        {
            float nRow = 0f;
            float gapL = 20f, gapR = 20f;
            float lmax = float.NegativeInfinity, rmin = float.PositiveInfinity;
            bool hasL = false, hasR = false;
            for (int j = 0; j < n; j++)
            {
                bool same = MathF.Abs(mid[j] - mid[i]) < 0.5f * MathF.Max(h[j], h[i]);
                if (!same)
                {
                    continue;
                }

                nRow += 1f;
                if (j == i)
                {
                    continue;
                }

                if (b[j][2] <= b[i][0] + (0.5f * h[i]))
                {
                    hasL = true;
                    lmax = MathF.Max(lmax, b[j][2]);
                }

                if (b[j][0] >= b[i][2] - (0.5f * h[i]))
                {
                    hasR = true;
                    rmin = MathF.Min(rmin, b[j][0]);
                }
            }

            if (hasL)
            {
                gapL = MathF.Min(20f, (b[i][0] - lmax) / hm);
            }

            if (hasR)
            {
                gapR = MathF.Min(20f, (rmin - b[i][2]) / hm);
            }

            float cx = (b[i][0] + b[i][2]) / 2f / wPg;
            float cy = (b[i][1] + b[i][3]) / 2f / hPg;
            string t = units[i].Text;
            Rune[] chars = [.. t.EnumerateRunes()];
            float len = chars.Length;
            float nAlpha = chars.Count(c => Rune.IsLetter(c));
            float dig = chars.Count(IsDigit) / MathF.Max(len, 1f);
            float alpha = nAlpha / MathF.Max(len, 1f);
            float upper = chars.Count(c => Rune.IsUpper(c)) / MathF.Max(nAlpha, 1f);
            float words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            float numeric = IsNumericText(t) ? 1f : 0f;
            string tr = t.TrimEnd();
            float endp = tr.EndsWith('.') || tr.EndsWith(':') || tr.EndsWith(';') || tr.EndsWith(',') ? 1f : 0f;
            Span<float> row = f.AsSpan(i * Dim, Dim);
            float[] baseF =
            [
                cx,
                cy,
                (b[i][2] - b[i][0]) / wPg,
                (b[i][3] - b[i][1]) / hPg,
                b[i][0] / wPg,
                b[i][2] / wPg,
                b[i][1] / hPg,
                b[i][3] / hPg,
                h[i] / hm,
                MathF.Log(1f + len) / 5f,
                MathF.Log(1f + words) / 3f,
                dig,
                alpha,
                upper,
                numeric,
                endp,
                MathF.Log(1f + nRow) / 3f,
                gapL / 20f,
                gapR / 20f,
                units[i].Bold == true ? 1f : 0f,
                units[i].Bold is null ? 0f : 1f,
            ];
            baseF.CopyTo(row);
            int k = 21;
            for (int q = 0; q < NFourier; q++)
            {
                float fr = MathF.Pow(2f, q) * MathF.PI;
                row[k] = MathF.Sin(fr * cx);
                row[k + 1] = MathF.Cos(fr * cx);
                row[k + 2] = MathF.Sin(fr * cy);
                row[k + 3] = MathF.Cos(fr * cy);
                k += 4;
            }

            // projection signals (8) stay zero
            k += 8;
            if (ruleF is not null)
            {
                ruleF[i].CopyTo(row[k..]);
                row[k + 4] = 1f;
            }

            k += 5;
            row[k] = -1f;
            row[k + 1] = -1f;
        }

        return f;
    }
}
