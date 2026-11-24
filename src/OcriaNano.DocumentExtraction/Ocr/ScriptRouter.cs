// Ported from LiteParse crates/liteparse/src/ocr/ocria/rec.rs (Router), Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>The script router: a script group per line and whether the page is upside down (<c>lpocr_rec.LineRouter</c>).</summary>
internal sealed class ScriptRouter(SessionPool sessions)
{
    /// <summary>Script group names, by index.</summary>
    public static readonly string[] Groups = ["latin", "cjk", "rtl", "world"];

    private const int RouterWidth = 512;
    private const int RouterBatch = 64;

    private static void Prep(Img crop, Span<float> x)
    {
        Img g = CtcRecognizer.Prep(crop, 8, int.MaxValue);

        // tile short lines across the router width, then cut to it
        for (int y = 0; y < CtcRecognizer.Height; y++)
        {
            for (int xx = 0; xx < RouterWidth; xx++)
            {
                x[(y * RouterWidth) + xx] = (g.Data[(y * g.Width) + (xx % g.Width)] / 127.5f) - 1f;
            }
        }
    }

    private static void Softmax(ReadOnlySpan<float> z, Span<float> p)
    {
        float m = float.NegativeInfinity;
        foreach (float v in z)
        {
            m = MathF.Max(m, v);
        }

        float sum = 0;
        for (int i = 0; i < z.Length; i++)
        {
            p[i] = MathF.Exp(z[i] - m);
            sum += p[i];
        }

        for (int i = 0; i < z.Length; i++)
        {
            p[i] /= sum;
        }
    }

    private (List<float[]> Groups, List<float> Flip) Probs(IReadOnlyList<Img> crops, CancellationToken cancellationToken)
    {
        List<float[]> gs = [];
        List<float> fs = [];
        for (int start = 0; start < crops.Count; start += RouterBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(RouterBatch, crops.Count - start);
            using OrtInput<float> x = new(n, 1, CtcRecognizer.Height, RouterWidth);
            for (int j = 0; j < n; j++)
            {
                Prep(crops[start + j], x.Span.Slice(j * CtcRecognizer.Height * RouterWidth, CtcRecognizer.Height * RouterWidth));
            }

            using var outputs = sessions.Get(OcriaNanoModels.Router).Run([("x", x.Value)], "group", "flip");
            ReadOnlySpan<float> g = outputs.ElementAt(0).GetTensorDataAsSpan<float>();
            ReadOnlySpan<float> f = outputs.ElementAt(1).GetTensorDataAsSpan<float>();
            for (int j = 0; j < n; j++)
            {
                float[] p = new float[4];
                Softmax(g.Slice(j * 4, 4), p);
                gs.Add(p);
                float[] pf = new float[2];
                Softmax(f.Slice(j * 2, 2), pf);
                fs.Add(pf[1]);
            }
        }

        return (gs, fs);
    }

    /// <summary>
    /// Page-level routing (<c>LineRouter.route</c>): the group index per crop, whether to turn the page, and each
    /// group's share of the line width. Every line takes the page's dominant group unless it is long enough to judge
    /// and confidently another; Latin unless another group holds a clear majority of the line width.
    /// </summary>
    public (int[] Groups, bool Flip, double[] Share) Route(IReadOnlyList<Img> crops, CancellationToken cancellationToken = default)
    {
        const float Override = 0.9f;
        const double MinAspect = 4.0;
        const int FlipMinLines = 3;
        const double FlipVote = 0.7;
        const double OtherShare = 0.65;
        if (crops.Count == 0)
        {
            return ([], false, new double[4]);
        }

        (List<float[]> gp, List<float> fp) = Probs(crops, cancellationToken);
        double[] w = [.. crops.Select(c => (double)c.Width)];
        double wsum = w.Sum();
        double[] share = new double[4];
        for (int i = 0; i < gp.Count; i++)
        {
            for (int k = 0; k < 4; k++)
            {
                share[k] += gp[i][k] * w[i];
            }
        }

        for (int k = 0; k < 4; k++)
        {
            share[k] /= wsum;
        }

        int dom = Argmax(share);
        if (dom != 0 && share[dom] <= OtherShare)
        {
            dom = 0;
        }

        int[] groups = new int[crops.Count];
        for (int i = 0; i < groups.Length; i++)
        {
            float[] p = gp[i];
            int own = Argmax([.. p.Select(v => (double)v)]);
            float pmax = p.Max();
            groups[i] = own != dom && pmax > Override && crops[i].Width >= MinAspect * crops[i].Height ? own : dom;
        }

        double vote = 0;
        for (int i = 0; i < fp.Count; i++)
        {
            vote += fp[i] * w[i];
        }

        bool flip = crops.Count >= FlipMinLines && vote > FlipVote * wsum;
        return (groups, flip, share);
    }

    /// <summary>np.argmax: the first maximum.</summary>
    public static int Argmax(ReadOnlySpan<double> v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i] > v[best])
            {
                best = i;
            }
        }

        return best;
    }
}
