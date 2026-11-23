using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
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
    }

    /// <summary>Gets the media types this client reads.</summary>
    public static IReadOnlyList<string> SupportedMediaTypes { get; } =
        ["application/pdf", "image/png", "image/jpeg", "image/webp", "image/bmp", "image/gif", "image/tiff"];

    /// <inheritdoc />
    public override Task<ExtractionDocument> ExtractAsync(
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

        throw new NotImplementedException();
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
