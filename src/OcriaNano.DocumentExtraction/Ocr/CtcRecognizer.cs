// Ported from LiteParse crates/liteparse/src/ocr/ocria/rec.rs, Apache-2.0.
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>Line recognizers (CTC, decoded in-graph) and shared line preparation (<c>lpocr_rec.Recognizer</c>).</summary>
internal sealed class CtcRecognizer
{
    /// <summary>Recognizer input height.</summary>
    public const int Height = 32;

    private const int Stride = 4;
    private const int MaxWidth = 1280;
    private const int BucketWidth = 128;
    private const int Batch = 32;

    private readonly InferenceSession _session;
    private readonly string[] _chars;
    private readonly bool _visualRtl;
    private readonly int _stride;

    private CtcRecognizer(InferenceSession session, string[] chars, bool visualRtl)
    {
        _session = session;
        _chars = chars;
        _visualRtl = visualRtl;

        // timestep stride from one probe run: input width / output steps
        using OrtInput<float> probe = new(1, 1, Height, 64);
        probe.Span.Fill(1f);
        using var outputs = session.Run([("x", probe.Value)], "ids");
        long steps = outputs.First().GetShape()[1];
        _stride = (int)(64 / Math.Max(steps, 1));
    }

    /// <summary>Loads <c>rec_&lt;group&gt;.onnx</c> with its charset and meta; null when that recognizer isn't installed.</summary>
    public static CtcRecognizer? Load(SessionPool sessions, string group)
    {
        string model = $"rec_{group}.onnx";
        string charsetName = $"rec_{group}.charset.json";
        if (!sessions.Exists(model) || !sessions.Exists(charsetName))
        {
            return null;
        }

        string[] chars;
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(sessions.GetFilePath(charsetName))))
        {
            chars = [.. json.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        }

        bool visualRtl = false;
        string metaName = $"rec_{group}.meta.json";
        if (sessions.Exists(metaName))
        {
            try
            {
                using JsonDocument meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sessions.Directory, metaName)));
                visualRtl = meta.RootElement.ValueKind == JsonValueKind.Object
                    && meta.RootElement.TryGetProperty("visual_rtl", out JsonElement v) && v.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
            }
        }

        return new CtcRecognizer(sessions.Get(model), chars, visualRtl);
    }

    /// <summary>A grey line image at height 32, width scaled with the aspect ratio (<c>Recognizer._prep</c>).</summary>
    public static Img Prep(Img crop, int minWidth, int maxWidth)
    {
        Img g = crop.ToGray();
        double scaled = Math.Round(g.Width * (double)Height / Math.Max(g.Height, 1), MidpointRounding.ToEven);
        int nw = (int)Math.Clamp(scaled, minWidth, maxWidth);
        return g.Height > Height ? Resize.Area(g, nw, Height) : Resize.Linear(g, nw, Height);
    }

    /// <summary>
    /// (text, confidence) per crop. Lines are batched by width (sorted, 32 per batch, padded to a multiple of 128 px)
    /// exactly as the reference does: a line's output depends on the padded width it was run at.
    /// </summary>
    public (string Text, float Confidence)[] Read(IReadOnlyList<Img> crops, CancellationToken cancellationToken = default)
    {
        Img[] lines = [.. crops.Select(c => Prep(c, Stride * 2, MaxWidth))];
        int[] order = [.. Enumerable.Range(0, lines.Length).OrderBy(i => lines[i].Width)];
        var result = new (string, float)[lines.Length];
        for (int start = 0; start < order.Length; start += Batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] idx = order[start..Math.Min(start + Batch, order.Length)];
            int maxw = idx.Max(i => lines[i].Width);
            int w = (maxw + BucketWidth - 1) / BucketWidth * BucketWidth;
            using OrtInput<float> x = new(idx.Length, 1, Height, w);
            Span<float> xs = x.Span;
            xs.Fill(1f);
            for (int j = 0; j < idx.Length; j++)
            {
                Img l = lines[idx[j]];
                for (int y = 0; y < Height; y++)
                {
                    for (int xx = 0; xx < l.Width; xx++)
                    {
                        // f64 then f32, as the reference does
                        xs[(((j * Height) + y) * w) + xx] = (float)((l.Data[(y * l.Width) + xx] / 127.5) - 1.0);
                    }
                }
            }

            using var outputs = _session.Run([("x", x.Value)], "ids", "conf");
            OrtValue idsValue = outputs.ElementAt(0);
            int tAll = (int)idsValue.GetShape()[1];
            ReadOnlySpan<long> ids = idsValue.GetTensorDataAsSpan<long>();
            ReadOnlySpan<float> conf = outputs.ElementAt(1).GetTensorDataAsSpan<float>();
            for (int j = 0; j < idx.Length; j++)
            {
                int i = idx[j];
                int t = Math.Min(lines[i].Width / _stride, tAll);
                StringBuilder text = new();
                double sum = 0;
                int kept = 0;
                long prev = 0;
                for (int k = 0; k < t; k++)
                {
                    long c = ids[(j * tAll) + k];
                    if (c != 0 && c != prev)
                    {
                        if ((ulong)(c - 1) < (ulong)_chars.Length)
                        {
                            text.Append(_chars[c - 1]);
                        }

                        sum += conf[(j * tAll) + k];
                        kept++;
                    }

                    prev = c;
                }

                string s = text.ToString();
                if (_visualRtl && s.EnumerateRunes().Any(Bidi.IsRtlChar))
                {
                    s = Bidi.VisualToLogical(s);
                }

                result[i] = (s, kept == 0 ? 0f : (float)(sum / kept));
            }
        }

        return result;
    }
}
