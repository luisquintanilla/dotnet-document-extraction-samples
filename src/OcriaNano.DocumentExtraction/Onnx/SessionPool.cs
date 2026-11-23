using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace OcriaNano.DocumentExtraction.Onnx;

/// <summary>One lazily created <see cref="InferenceSession"/> per model file. <see cref="InferenceSession.Run(RunOptions, IReadOnlyCollection{string}, IReadOnlyCollection{OrtValue}, IReadOnlyCollection{string})"/> is thread-safe, so sessions are shared across pages.</summary>
internal sealed class SessionPool : IDisposable
{
    private readonly string _directory;
    private readonly bool _verify;
    private readonly SessionOptions? _borrowed;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);
    private SessionOptions? _owned;
    private bool _disposed;

    public SessionPool(string directory, bool verifyHashes, SessionOptions? sessionOptions)
    {
        _directory = directory;
        _verify = verifyHashes;
        _borrowed = sessionOptions;
    }

    public string Directory => _directory;

    public bool Exists(string name) => OcriaNanoModels.Exists(_directory, name);

    public string GetFilePath(string name) => OcriaNanoModels.GetFile(_directory, name, _verify);

    public InferenceSession Get(string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(name, out InferenceSession? session))
            {
                string path = OcriaNanoModels.GetFile(_directory, name, _verify);
                session = new InferenceSession(path, _borrowed ?? GetOwnedOptions());
                if (name == OcriaNanoModels.TableNet)
                {
                    CheckTableNet(session);
                }

                _sessions[name] = session;
            }

            return session;
        }
    }

    public IReadOnlyCollection<string> LoadedModels
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Keys];
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (InferenceSession session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
            _owned?.Dispose();
        }
    }

    private SessionOptions GetOwnedOptions()
    {
        if (_owned is null)
        {
            _owned = new SessionOptions
            {
                // LiteParse runs the graphs with fresh shapes per page; an arena just holds on to the peak.
                EnableMemoryPattern = false,
                EnableCpuMemArena = false,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
        }

        return _owned;
    }

    // The LayoutNet decoder hard-codes the feature layout and class list, so refuse a graph built for another one.
    internal static void CheckTableNet(InferenceSession session)
    {
        if (!session.ModelMetadata.CustomMetadataMap.TryGetValue("liteparse.tablenet", out string? meta))
        {
            return;
        }

        using JsonDocument json = JsonDocument.Parse(meta);
        int featDim = json.RootElement.GetProperty("feat_dim").GetInt32();
        int classes = json.RootElement.GetProperty("cfg").GetProperty("n_cls").GetInt32();
        if (featDim != Layout.UnitFeatures.Dim || classes != Layout.LayoutClasses.Count)
        {
            throw new InvalidDataException(
                $"tablenet.onnx expects {featDim} features and {classes} classes; this library supports " +
                $"{Layout.UnitFeatures.Dim} and {Layout.LayoutClasses.Count}.");
        }
    }
}
