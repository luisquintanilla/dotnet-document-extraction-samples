// Contract for the OCRia-nano OCR port (LiteParse crates/liteparse/src/ocr/ocria/*), Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// The OCRia-nano engine (port of <c>OcriaEngine::run</c>): detect lines, turn sideways pages, split gutters, route
/// each line to a script recognizer, recognize, clean up and rescue. Thread-safe.
/// </summary>
internal sealed partial class OcriaEngine
{
    public OcriaEngine(SessionPool sessions, OcriaScripts scripts)
    {
        Sessions = sessions;
        Scripts = scripts | OcriaScripts.Latin;
    }

    public SessionPool Sessions { get; }

    public OcriaScripts Scripts { get; }

    /// <summary>Recognizes the text on a page image. Line boxes and quads are in the image's pixels.</summary>
    public OcrPage Recognize(RgbImage image, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
