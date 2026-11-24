// Ported from LiteParse crates/liteparse/src/ocr/ocria/det.rs, Apache-2.0.
namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// Joins near-horizontal line quads that continue one another (<c>lpocr_rec._merge_collinear</c>): each box links to its
/// nearest qualifying right neighbour when that neighbour's nearest left neighbour is the same box, and each chain
/// becomes the rectangle spanning it along its longest member's axis.
/// </summary>
internal static class LineMerger
{
    private const float MaxTilt = 0.35f;
    private const float MaxPerp = 0.35f;
    private const float MinHeightRatio = 0.6f;
    private const float MaxDangle = 0.07f;

    private static readonly float s_cosMin = 1f - (MaxDangle * MaxDangle / 2f);

    public static List<PointF[]> Merge(IReadOnlyList<PointF[]> boxes, float gap)
    {
        int n = boxes.Count;
        if (n < 2)
        {
            return [.. boxes];
        }

        float[] cx = new float[n], cy = new float[n], ux = new float[n], uy = new float[n], len = new float[n], hgt = new float[n];
        for (int i = 0; i < n; i++)
        {
            PointF[] q = boxes[i];
            cx[i] = (q[0].X + q[1].X + q[2].X + q[3].X) / 4f;
            cy[i] = (q[0].Y + q[1].Y + q[2].Y + q[3].Y) / 4f;
            float dx = q[1].X - q[0].X, dy = q[1].Y - q[0].Y;
            float l = MathF.Max(Quads.Norm(dx, dy), 1e-3f);
            ux[i] = dx / l;
            uy[i] = dy / l;
            len[i] = l;
            hgt[i] = MathF.Max(Quads.Norm(q[3].X - q[0].X, q[3].Y - q[0].Y), 1e-3f);
        }

        // v = (-uy, ux)
        bool[] flat = new bool[n];
        for (int i = 0; i < n; i++)
        {
            flat[i] = MathF.Abs(uy[i]) <= MaxTilt && len[i] >= 0.5f * hgt[i];
        }

        int[] right = new int[n];
        (float G, int A)[] leftBest = new (float, int)[n];
        Array.Fill(leftBest, (float.PositiveInfinity, -1));
        for (int a = 0; a < n; a++)
        {
            (float g, int b) best = (float.PositiveInfinity, -1);
            if (flat[a])
            {
                float vax = -uy[a], vay = ux[a];
                for (int b = 0; b < n; b++)
                {
                    if (a == b || !flat[b])
                    {
                        continue;
                    }

                    float dx = cx[b] - cx[a], dy = cy[b] - cy[a];
                    float along = (dx * ux[a]) + (dy * uy[a]);
                    float perp = MathF.Abs((dx * vax) + (dy * vay));
                    float hm = (hgt[a] + hgt[b]) / 2f;
                    float g = along - ((len[a] + len[b]) / 2f);
                    bool ok = along > 0f
                        && MathF.Min(hgt[a], hgt[b]) / MathF.Max(hgt[a], hgt[b]) >= MinHeightRatio
                        && MathF.Abs((ux[a] * ux[b]) + (uy[a] * uy[b])) >= s_cosMin
                        && perp <= MaxPerp * hm
                        && g <= gap * hm
                        && g >= -0.5f * MathF.Min(len[a], len[b]);
                    if (ok)
                    {
                        if (g < best.g)
                        {
                            best = (g, b);
                        }

                        // column-wise argmin for b: first a reaching the minimum
                        if (g < leftBest[b].G)
                        {
                            leftBest[b] = (g, a);
                        }
                    }
                }
            }

            right[a] = best.b;
        }

        int[] next = new int[n];
        Array.Fill(next, -1);
        for (int a = 0; a < n; a++)
        {
            int b = right[a];
            if (b >= 0 && leftBest[b].A == a)
            {
                next[a] = b;
            }
        }

        bool[] hasPrev = new bool[n];
        foreach (int b in next)
        {
            if (b >= 0)
            {
                hasPrev[b] = true;
            }
        }

        List<PointF[]> output = [];
        bool[] seen = new bool[n];
        for (int a = 0; a < n; a++)
        {
            if (hasPrev[a] || seen[a])
            {
                continue;
            }

            List<int> chain = [a];
            seen[a] = true;
            while (true)
            {
                int nx = next[chain[^1]];
                if (nx < 0 || seen[nx])
                {
                    break;
                }

                chain.Add(nx);
                seen[nx] = true;
            }

            if (chain.Count == 1)
            {
                output.Add(boxes[a]);
                continue;
            }

            // the chain's direction is its longest member's (first one on ties, as max())
            int k = chain[0];
            foreach (int i in chain.Skip(1))
            {
                if (len[i] > len[k])
                {
                    k = i;
                }
            }

            float np = chain.Count * 4;
            float sx = 0f, sy = 0f;
            foreach (int i in chain)
            {
                foreach (PointF p in boxes[i])
                {
                    sx += p.X;
                    sy += p.Y;
                }
            }

            float cax = sx / np, cay = sy / np;
            float uax = ux[k], uay = uy[k];
            float vax = -uay, vay = uax;
            float a0 = float.PositiveInfinity, a1 = float.NegativeInfinity, b0 = float.PositiveInfinity, b1 = float.NegativeInfinity;
            foreach (int i in chain)
            {
                foreach (PointF p in boxes[i])
                {
                    float dx = p.X - cax, dy = p.Y - cay;
                    float pa = (dx * uax) + (dy * uay);
                    float pb = (dx * vax) + (dy * vay);
                    a0 = MathF.Min(a0, pa);
                    a1 = MathF.Max(a1, pa);
                    b0 = MathF.Min(b0, pb);
                    b1 = MathF.Max(b1, pb);
                }
            }

            PointF At(float s, float t) => new(cax + (uax * s) + (vax * t), cay + (uay * s) + (vay * t));
            output.Add(Quads.Order([At(a0, b0), At(a1, b0), At(a1, b1), At(a0, b1)]));
        }

        for (int i = 0; i < n; i++)
        {
            if (!seen[i])
            {
                output.Add(boxes[i]);
            }
        }

        return output;
    }
}
