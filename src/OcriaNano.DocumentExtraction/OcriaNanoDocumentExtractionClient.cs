using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using System.Globalization;
using OcriaNano.DocumentExtraction.Document;
using OcriaNano.DocumentExtraction.Ocr;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction;

/// <summary>
/// A <see cref="DocumentExtractionClient"/> that runs LlamaIndex's OCRia-nano-1 models locally with ONNX Runtime:
/// text detection, script routing and recognition for scanned pages and images, and LayoutNet for blocks and tables.
/// Born-digital PDF pages keep their text layer.
/// </summary>
/// <remarks>The models aren't included; see <see cref="OcriaNanoModels"/>. They're licensed separately by LlamaIndex.</remarks>
public sealed class OcriaNanoDocumentExtractionClient : DocumentExtractionClient
{
    private readonly OcriaNanoOptions _options;
    private readonly Lazy<SessionPool> _sessions;

    /// <summary>Initializes a new instance of the <see cref="OcriaNanoDocumentExtractionClient"/> class.</summary>
    /// <param name="options">Settings, or null for the defaults. The client keeps a copy.</param>
    public OcriaNanoDocumentExtractionClient(OcriaNanoOptions? options = null)
    {
        _options = options?.Clone() ?? new OcriaNanoOptions();
        _sessions = new Lazy<SessionPool>(CreateSessions, LazyThreadSafetyMode.ExecutionAndPublication);
        _engine = new Lazy<OcriaEngine>(() => new OcriaEngine(Sessions, _options.Scripts), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly Lazy<OcriaEngine> _engine;

    /// <summary>Gets the media types this client reads.</summary>
    public static IReadOnlyList<string> SupportedMediaTypes { get; } =
        ["application/pdf", "image/png", "image/jpeg", "image/webp", "image/bmp"];

    /// <summary>Per-call override for <see cref="OcriaNanoOptions.Ocr"/>: <c>auto</c>, <c>always</c> or <c>never</c>.</summary>
    public const string OcrProperty = "ocria.ocr";

    /// <summary>Per-call override for <see cref="OcriaNanoOptions.OcrDpi"/>.</summary>
    public const string DpiProperty = "ocria.dpi";

    /// <summary>Per-call override for <see cref="OcriaNanoOptions.EnableLayout"/>.</summary>
    public const string LayoutProperty = "ocria.layout";

    /// <summary>Per-call 1-based PDF page selection, such as <c>"1-3,5"</c>.</summary>
    public const string PagesProperty = "ocria.pages";

    /// <summary>Per-call override for <see cref="OcriaNanoOptions.IncludeFigureImages"/>.</summary>
    public const string FigureImagesProperty = "ocria.figureImages";

    /// <inheritdoc />
    /// <remarks>
    /// PDF pages keep their text layer unless the OCR gate (or <see cref="OcriaNanoOptions.Ocr"/>) sends them to OCR;
    /// images are always OCR'd and treated as a page whose long side is 792 points, with pixel source coordinates.
    /// <see cref="DocumentExtractionOptions.AdditionalProperties"/> may override settings per call with
    /// <see cref="OcrProperty"/>, <see cref="DpiProperty"/>, <see cref="LayoutProperty"/>, <see cref="PagesProperty"/>
    /// and <see cref="FigureImagesProperty"/>.
    /// </remarks>
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        string normalized = NormalizeMediaType(mediaType);
        if (!SupportedMediaTypes.Contains(normalized))
        {
            throw new NotSupportedException($"OCRia-nano reads {string.Join(", ", SupportedMediaTypes)}; got '{mediaType}'.");
        }

        ExtractionSettings settings = Settings(_options, options?.AdditionalProperties);
        byte[] bytes;
        using (MemoryStream ms = new())
        {
            await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        ExtractionPipeline pipeline = new(Sessions, _engine.Value);
        return await Task.Run(
            () => normalized == "application/pdf"
                ? pipeline.ExtractPdf(bytes, identifier, settings, cancellationToken)
                : pipeline.ExtractImage(bytes, identifier, settings, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal static ExtractionSettings Settings(OcriaNanoOptions options, IReadOnlyDictionary<string, object?>? props)
    {
        OcrMode ocr = options.Ocr;
        int dpi = options.OcrDpi;
        bool layout = options.EnableLayout;
        bool figures = options.IncludeFigureImages;
        string? pages = null;
        if (props is not null)
        {
            if (props.TryGetValue(OcrProperty, out object? o) && o is not null)
            {
                ocr = o is OcrMode m ? m : Enum.Parse<OcrMode>(Convert.ToString(o, CultureInfo.InvariantCulture)!, ignoreCase: true);
            }

            if (props.TryGetValue(DpiProperty, out object? d) && d is not null)
            {
                dpi = Convert.ToInt32(d, CultureInfo.InvariantCulture);
            }

            if (props.TryGetValue(LayoutProperty, out object? l) && l is not null)
            {
                layout = Convert.ToBoolean(l, CultureInfo.InvariantCulture);
            }

            if (props.TryGetValue(PagesProperty, out object? p) && p is not null)
            {
                pages = Convert.ToString(p, CultureInfo.InvariantCulture);
            }

            if (props.TryGetValue(FigureImagesProperty, out object? f) && f is not null)
            {
                figures = Convert.ToBoolean(f, CultureInfo.InvariantCulture);
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 36, DpiProperty);
        return new ExtractionSettings(ocr, dpi, layout, pages, Math.Max(1, options.MaxDegreeOfParallelism), figures);
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(OcriaNanoOptions))
        {
            return _options.Clone();
        }

        return serviceType.IsInstanceOfType(this) ? this : base.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _sessions.IsValueCreated)
        {
            _sessions.Value.Dispose();
        }

        base.Dispose(disposing);
    }

    internal SessionPool Sessions => _sessions.Value;

    internal static string NormalizeMediaType(string mediaType)
    {
        int semi = mediaType.IndexOf(';');
        string type = (semi < 0 ? mediaType : mediaType[..semi]).Trim().ToLowerInvariant();
        return type switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/x-ms-bmp" => "image/bmp",
            "image/tif" => "image/tiff",
            _ => type,
        };
    }

    private SessionPool CreateSessions()
    {
        if (!OcriaNanoModels.TryResolve(_options.ModelDirectory, out string? directory, out string? reason))
        {
            throw new InvalidOperationException($"OCRia-nano models aren't available: {reason}.");
        }

        return new SessionPool(directory!, _options.VerifyModelHashes, _options.SessionOptions);
    }
}
