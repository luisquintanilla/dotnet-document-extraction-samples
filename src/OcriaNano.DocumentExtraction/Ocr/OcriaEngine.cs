// Ported from LiteParse crates/liteparse/src/ocr/ocria/mod.rs, Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// The OCRia-nano engine (port of <c>OcriaEngine::run</c>): detect lines, turn sideways pages, split gutters, route
/// each line to a script recognizer, recognize, clean up and rescue. Thread-safe.
/// </summary>
internal sealed partial class OcriaEngine
{
    private readonly Lazy<TextDetector> _detector;
    private readonly ScriptRouter _router;

    // One recognizer per script group (ScriptRouter.Groups order); a group that is disabled or not installed reads with Latin.
    private readonly Lazy<CtcRecognizer?>[] _recognizers;

    public OcriaEngine(SessionPool sessions, OcriaScripts scripts)
    {
        Sessions = sessions;
        Scripts = scripts | OcriaScripts.Latin;
        _detector = new Lazy<TextDetector>(() => TextDetector.Create(sessions));
        _router = new ScriptRouter(sessions);
        _recognizers = new Lazy<CtcRecognizer?>[ScriptRouter.Groups.Length];
        OcriaScripts[] flags = [OcriaScripts.Latin, OcriaScripts.Cjk, OcriaScripts.Rtl, OcriaScripts.World];
        for (int g = 0; g < _recognizers.Length; g++)
        {
            string name = ScriptRouter.Groups[g];
            bool enabled = (Scripts & flags[g]) != 0;
            _recognizers[g] = new Lazy<CtcRecognizer?>(() => enabled ? CtcRecognizer.Load(sessions, name) : null);
        }
    }

    public SessionPool Sessions { get; }

    public OcriaScripts Scripts { get; }

    /// <summary>Recognizes the text on a page image. Line boxes and quads are in the image's pixels.</summary>
    public OcrPage Recognize(RgbImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        Img img0 = Img.FromRgb(image);
        TextDetector detector = _detector.Value;
        List<PointF[]> boxes = detector.Detect(img0, cancellationToken);
        bool sideways = false;
        Img img = img0;
        if (PageFrame.LooksSideways(boxes))
        {
            // maybe a page scanned on its side: detect again a quarter turn counter-clockwise and keep that when it
            // finds far more lines (the router's flip vote finishes 270 degrees)
            Img turned = img0.Rot90(1);
            List<PointF[]> tboxes = detector.Detect(turned, cancellationToken);
            if (PageFrame.LongLines(tboxes) > (2 * PageFrame.LongLines(boxes)) + 5)
            {
                img = turned;
                boxes = tboxes;
                sideways = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        boxes = PageFrame.SplitGutters(img, boxes);
        (List<(string Text, float Confidence, int Group)> reads, bool flip) = boxes.Count == 0
            ? ([], false)
            : Read(img, boxes, cancellationToken);

        List<OcrLine> lines = [];
        for (int i = 0; i < boxes.Count; i++)
        {
            (string text, float conf, int group) = reads[i];
            if (text.Trim().Length == 0 || PageFrame.IsJunk(text, conf))
            {
                continue;
            }

            PointF[] pts = PageFrame.ReadingFrame(boxes[i], flip, sideways, img0.Width);
            float x1 = float.PositiveInfinity, y1 = float.PositiveInfinity, x2 = float.NegativeInfinity, y2 = float.NegativeInfinity;
            foreach (PointF p in pts)
            {
                x1 = MathF.Min(x1, p.X);
                y1 = MathF.Min(y1, p.Y);
                x2 = MathF.Max(x2, p.X);
                y2 = MathF.Max(y2, p.Y);
            }

            // the server sends int() of each extent
            lines.Add(new OcrLine(
                text,
                new BoxF(MathF.Truncate(x1), MathF.Truncate(y1), MathF.Truncate(x2), MathF.Truncate(y2)),
                pts,
                conf,
                0,
                ScriptRouter.Groups[group]));
        }

        return new OcrPage(image.Width, image.Height, lines);
    }

    private CtcRecognizer RecognizerFor(int group)
        => _recognizers[group].Value
            ?? _recognizers[0].Value
            ?? throw new FileNotFoundException($"The Latin OCRia-nano recognizer (rec_latin.onnx) isn't in {Sessions.Directory}; {OcriaNanoModels.InstallHint}.");

    private bool IsInstalled(int group) => group == 0 || _recognizers[group].Value is not null;
}
