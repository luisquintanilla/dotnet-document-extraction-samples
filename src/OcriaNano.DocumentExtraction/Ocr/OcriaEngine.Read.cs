// Ported from LiteParse crates/liteparse/src/ocr/ocria/mod.rs (OcriaEngine::read), Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;

namespace OcriaNano.DocumentExtraction.Ocr;

internal sealed partial class OcriaEngine
{
    // a routed reading this confident is a real reading, not the 0.5-0.7 junk of the wrong recognizer
    private const float KeepConfidence = 0.75f;

    private static double Mean(IEnumerable<float> v)
    {
        double sum = 0;
        int n = 0;
        foreach (float x in v)
        {
            sum += x;
            n++;
        }

        return n == 0 ? double.NaN : sum / n;
    }

    /// <summary>Readings per box (with the script group each was read as) and whether the page was read upside down (<c>server.read</c>).</summary>
    private (List<(string Text, float Confidence, int Group)> Reads, bool Flip) Read(Img img, IReadOnlyList<PointF[]> boxes, CancellationToken ct)
    {
        List<Img> crops = [.. boxes.Select(b => PageFrame.Crop(img, b))];
        (int[] groups, bool flip, double[] share) = _router.Route(crops, ct);
        if (flip)
        {
            crops = [.. crops.Select(c => c.Rot90(2))];
        }

        int n = crops.Count;
        (string Text, float Conf)[] output = new (string, float)[n];
        output.AsSpan().Fill((string.Empty, 0f));
        List<Img> Subset(List<int> idx) => [.. idx.Select(i => crops[i])];
        List<int> Where(Func<int, bool> pred) => [.. Enumerable.Range(0, n).Where(pred)];

        for (int gi = 0; gi < ScriptRouter.Groups.Length; gi++)
        {
            ct.ThrowIfCancellationRequested();
            List<int> idx = Where(i => groups[i] == gi);
            if (idx.Count == 0)
            {
                continue;
            }

            var res = RecognizerFor(gi).Read(Subset(idx), ct);
            for (int k = 0; k < idx.Count; k++)
            {
                output[idx[k]] = res[k];
            }
        }

        int dom = PageFrame.Majority(groups);
        if (dom == 0)
        {
            // a whole page of another script sent to the Latin recognizer reads as confident-looking junk at
            // 0.5-0.7; the right recognizer then scores 0.83-0.90
            const double RescueConf = 0.75;
            const double RescueMargin = 0.15;

            // only scripts the router saw a fair share of are tried
            const double RescueShare = 0.2;
            List<int> lat = Where(i => groups[i] == dom);
            if (lat.Count > 0 && share.AsSpan(1).ToArray().Any(s => s >= RescueShare))
            {
                double latConf = Mean(lat.Select(i => output[i].Conf));
                if (latConf < RescueConf)
                {
                    List<int> sample = [.. lat.Take(40)];
                    int best = -1;
                    double bestConf = latConf + RescueMargin;
                    for (int gi = 1; gi < ScriptRouter.Groups.Length; gi++)
                    {
                        if (share[gi] < RescueShare || !IsInstalled(gi))
                        {
                            continue;
                        }

                        double c = Mean(RecognizerFor(gi).Read(Subset(sample), ct).Select(r => r.Confidence));
                        if (c >= bestConf)
                        {
                            best = gi;
                            bestConf = c;
                        }
                    }

                    if (best >= 0)
                    {
                        var res = RecognizerFor(best).Read(Subset(lat), ct);
                        for (int k = 0; k < lat.Count; k++)
                        {
                            int i = lat[k];

                            // per line: a bilingual page keeps its confident Latin lines
                            if (!(output[i].Conf >= KeepConfidence && output[i].Conf >= res[k].Confidence))
                            {
                                output[i] = res[k];
                                groups[i] = best;
                            }
                        }

                        dom = best;
                    }
                }
            }

            // per-line rescue when the page stays Latin: a map or form mixing English labels with another script
            if (dom == 0)
            {
                const float LineMargin = 0.1f;
                const double LineShare = 0.15;
                const float LineConf = 0.8f;
                for (int gi = 1; gi < ScriptRouter.Groups.Length; gi++)
                {
                    if (share[gi] < LineShare || !IsInstalled(gi))
                    {
                        continue;
                    }

                    List<int> weak = Where(i => groups[i] == 0);
                    if (weak.Count == 0)
                    {
                        break;
                    }

                    var res = RecognizerFor(gi).Read(Subset(weak), ct);
                    for (int k = 0; k < weak.Count; k++)
                    {
                        int i = weak[k];
                        (string text, float conf) = res[k];

                        // a switch needs the margin and the script's own characters
                        if (conf >= output[i].Conf + LineMargin
                            && conf >= LineConf
                            && PageFrame.ScriptChars(gi, text) >= 3
                            && PageFrame.ScriptChars(gi, text) > LatinCleanup.LatinLetters(text))
                        {
                            output[i] = res[k];
                            groups[i] = gi;
                        }
                    }
                }
            }
        }

        // lines routed away from the page's script are read with the page's recognizer too; the off-script reading must win clearly
        List<int> odd = Where(i => groups[i] != dom);
        if (odd.Count > 0)
        {
            var res = RecognizerFor(dom).Read(Subset(odd), ct);
            for (int k = 0; k < odd.Count; k++)
            {
                int i = odd[k];

                // a confident reading in the routed script stands, if it is mostly that script
                bool routedOk = output[i].Conf >= KeepConfidence
                    && PageFrame.ScriptChars(groups[i], output[i].Text) >= 2
                    && PageFrame.ScriptChars(groups[i], output[i].Text) > LatinCleanup.LatinLetters(output[i].Text);
                bool replace = !routedOk
                    && (output[i].Conf < res[k].Confidence + 0.1 || PageFrame.ScriptChars(groups[i], output[i].Text) < 3);
                if (replace)
                {
                    output[i] = res[k];
                }
            }
        }

        List<(string, float, int)> reads = new(n);
        for (int i = 0; i < n; i++)
        {
            reads.Add((LatinCleanup.CleanLatin(output[i].Text), output[i].Conf, groups[i]));
        }

        return (reads, flip);
    }
}
