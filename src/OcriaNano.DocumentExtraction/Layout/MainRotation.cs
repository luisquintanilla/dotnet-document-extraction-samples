// Ported from LiteParse crates/liteparse/src/table_model/mod.rs (main_rotation, rotate_rules, run_page), Apache-2.0.
using System.Text;

namespace OcriaNano.DocumentExtraction.Layout;

internal static class MainRotation
{
    /// <summary>The rotated reading direction (1..3) carrying at least 60% of a text-layer page's characters, if any.</summary>
    public static int? Detect(IReadOnlyList<Unit> units, bool ocr)
    {
        if (ocr)
        {
            return null;
        }

        int[] byQ = new int[4];
        foreach (Unit u in units)
        {
            byQ[u.Q & 3] += u.Text.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r));
        }

        int total = byQ.Sum();
        for (int q = 1; q < 4; q++)
        {
            if (total > 0 && byQ[q] * 10 >= total * 6)
            {
                return q;
            }
        }

        return null;
    }

    /// <summary>Ruling lines in reading direction <paramref name="q"/>'s frame; a quarter turn swaps horizontal and vertical.</summary>
    public static Rules RotateRules(Rules r, int q, double w, double h)
    {
        Rules output = new();
        IEnumerable<Box4> segs = r.H.Select(s => new Box4(s.A0, s.P, s.A1, s.P))
            .Concat(r.V.Select(s => new Box4(s.P, s.A0, s.P, s.A1)));
        foreach (Box4 seg in segs)
        {
            Box4 b = Units.ToFrame(seg, q, w, h);
            float b0 = (float)b.X0, b1 = (float)b.Y0, b2 = (float)b.X1, b3 = (float)b.Y1;
            if (MathF.Abs(b3 - b1) <= MathF.Abs(b2 - b0))
            {
                output.H.Add(new RuleSegment((b1 + b3) / 2f, MathF.Min(b0, b2), MathF.Max(b0, b2)));
            }
            else
            {
                output.V.Add(new RuleSegment((b0 + b2) / 2f, MathF.Min(b1, b3), MathF.Max(b1, b3)));
            }
        }

        return output;
    }
}
