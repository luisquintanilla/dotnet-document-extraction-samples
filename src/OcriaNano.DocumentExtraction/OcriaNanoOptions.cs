using Microsoft.ML.OnnxRuntime;

namespace OcriaNano.DocumentExtraction;

/// <summary>When <see cref="OcriaNanoDocumentExtractionClient"/> runs OCR on a PDF page.</summary>
public enum OcrMode
{
    /// <summary>OCR only pages whose text layer is missing, thin, garbled or covered by images; keep native text elsewhere.</summary>
    Auto,

    /// <summary>OCR every page and ignore the PDF text layer.</summary>
    Always,

    /// <summary>Never OCR PDF pages; use the text layer only. Images are always OCR'd.</summary>
    Never,
}

/// <summary>Recognizers to use. Latin is always used.</summary>
[Flags]
public enum OcriaScripts
{
    /// <summary>Latin (English and other Latin-script languages, digits, punctuation).</summary>
    Latin = 1,

    /// <summary>Chinese, Japanese and Korean (<c>rec_cjk.onnx</c>).</summary>
    Cjk = 2,

    /// <summary>Arabic and Hebrew (<c>rec_rtl.onnx</c>).</summary>
    Rtl = 4,

    /// <summary>Other scripts: Cyrillic, Greek, Devanagari, Thai and more (<c>rec_world.onnx</c>).</summary>
    World = 8,

    /// <summary>Every recognizer.</summary>
    All = Latin | Cjk | Rtl | World,
}

/// <summary>Settings for <see cref="OcriaNanoDocumentExtractionClient"/>.</summary>
public sealed class OcriaNanoOptions
{
    /// <summary>
    /// Gets or sets the folder that holds the OCRia-nano-1 model files. When null, the client uses
    /// <c>$OCRIA_MODEL_DIR</c>, then <c>$LITEPARSE_MODEL_DIR/ocria-nano</c>, then LiteParse's default
    /// (<c>%LOCALAPPDATA%\liteparse\models\ocria-nano</c>, or <c>~/.cache/liteparse/models/ocria-nano</c>).
    /// </summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Gets or sets whether model files are checked against their pinned size and SHA-256 when first loaded.</summary>
    public bool VerifyModelHashes { get; set; } = true;

    /// <summary>Gets or sets the resolution PDF pages are rendered at for OCR.</summary>
    public int OcrDpi { get; set; } = 250;

    /// <summary>Gets or sets when PDF pages are OCR'd.</summary>
    public OcrMode Ocr { get; set; } = OcrMode.Auto;

    /// <summary>Gets or sets whether the LayoutNet model groups text into blocks and tables. When false, lines are grouped into paragraphs by spacing.</summary>
    public bool EnableLayout { get; set; } = true;

    /// <summary>
    /// Gets or sets which recognizers are used. Latin is always used. A line the router sends to a recognizer that
    /// isn't enabled, or whose model isn't installed, is read with the Latin recognizer.
    /// </summary>
    public OcriaScripts Scripts { get; set; } = OcriaScripts.Latin;

    /// <summary>Gets or sets ONNX Runtime session options. The client borrows them and doesn't dispose them.</summary>
    public SessionOptions? SessionOptions { get; set; }

    /// <summary>Gets or sets how many pages are processed at once.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    internal OcriaNanoOptions Clone() => (OcriaNanoOptions)MemberwiseClone();
}
