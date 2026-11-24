using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace OcriaNano.DocumentExtraction;

/// <summary>Finds the OCRia-nano-1 model files on disk and checks them against their pinned sizes and hashes. Never downloads.</summary>
public static class OcriaNanoModels
{
    /// <summary>The model release this library was written against.</summary>
    public const string Version = "1.0.0";

    /// <summary>The command that installs the models, for error messages.</summary>
    public const string InstallHint = "run tools/ocria/Get-OcriaModels.ps1 (or `lit models pull ocria-nano`)";

    internal const string Detector = "det.onnx";
    internal const string DetectorMeta = "det.meta.json";
    internal const string Router = "router.onnx";
    internal const string TableNet = "tablenet.onnx";

    private static readonly Lazy<IReadOnlyDictionary<string, ModelPin>> s_pins = new(LoadPins);
    private static readonly ConcurrentDictionary<string, bool> s_verified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the folder the client looks in when <see cref="OcriaNanoOptions.ModelDirectory"/> is null.</summary>
    /// <returns>The default model folder, or null when no home or local-app-data folder is known.</returns>
    public static string? GetDefaultDirectory()
    {
        if (Environment.GetEnvironmentVariable("OCRIA_MODEL_DIR") is { Length: > 0 } dir)
        {
            return dir;
        }

        if (Environment.GetEnvironmentVariable("LITEPARSE_MODEL_DIR") is { Length: > 0 } lp)
        {
            return Path.Combine(lp, "ocria-nano");
        }

        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return local.Length == 0 ? null : Path.Combine(local, "liteparse", "models", "ocria-nano");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? null : Path.Combine(home, ".cache", "liteparse", "models", "ocria-nano");
    }

    /// <summary>Checks whether the core models (detector, router, Latin recognizer and LayoutNet) are installed.</summary>
    /// <param name="modelDirectory">The folder to check, or null for <see cref="GetDefaultDirectory"/>.</param>
    /// <param name="directory">The folder that was checked.</param>
    /// <param name="reason">Why the models can't be used, or null when they can.</param>
    /// <returns>Whether every core file is present. Hashes are checked later, when the files are loaded.</returns>
    public static bool TryResolve(string? modelDirectory, out string? directory, out string? reason)
    {
        directory = modelDirectory ?? GetDefaultDirectory();
        if (directory is null)
        {
            reason = $"no model folder; set OCRIA_MODEL_DIR or {InstallHint}";
            return false;
        }

        foreach (ModelPin pin in s_pins.Value.Values)
        {
            if (pin.Script == "core" && !File.Exists(Path.Combine(directory, pin.Name)))
            {
                reason = $"models missing from {directory} ({pin.Name}); {InstallHint}";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>Returns the path to a model file, checking its size and SHA-256 once per process.</summary>
    internal static string GetFile(string directory, string name, bool verify)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"OCRia-nano model file {name} not found in {directory}; {InstallHint}.", path);
        }

        if (verify && s_pins.Value.TryGetValue(name, out ModelPin? pin) && !s_verified.ContainsKey(path))
        {
            long size = new FileInfo(path).Length;
            string hash;
            using (FileStream stream = File.OpenRead(path))
            {
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            if (size != pin.Size || hash != pin.Sha256)
            {
                throw new InvalidDataException(
                    $"{path} doesn't match OCRia-nano-1 {Version} (expected {pin.Size} bytes, sha256 {pin.Sha256}; " +
                    $"found {size} bytes, sha256 {hash}). Reinstall the models: {InstallHint}.");
            }

            s_verified[path] = true;
        }

        return path;
    }

    /// <summary>Whether an optional model file (a non-Latin recognizer) is present.</summary>
    internal static bool Exists(string directory, string name) => File.Exists(Path.Combine(directory, name));

    private static IReadOnlyDictionary<string, ModelPin> LoadPins()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OcriaNano.DocumentExtraction.models.json")
            ?? throw new InvalidOperationException("The embedded model manifest is missing.");
        using JsonDocument json = JsonDocument.Parse(stream);
        Dictionary<string, ModelPin> pins = new(StringComparer.Ordinal);
        foreach (JsonElement file in json.RootElement.GetProperty("files").EnumerateArray())
        {
            ModelPin pin = new(
                file.GetProperty("name").GetString()!,
                file.GetProperty("script").GetString()!,
                file.GetProperty("size").GetInt64(),
                file.GetProperty("sha256").GetString()!);
            pins[pin.Name] = pin;
        }

        return pins;
    }

    private sealed record ModelPin(string Name, string Script, long Size, string Sha256);
}
