namespace OcriaNano.DocumentExtraction.Tests;

/// <summary>Finds the installed models and the repository's sample books. Tests that need models skip when they're missing.</summary>
internal static class TestModels
{
    private static readonly Lazy<string?> s_dir = new(() =>
        OcriaNanoModels.TryResolve(null, out string? dir, out _) ? dir : null);

    public static string? Directory => s_dir.Value;

    public static string Require()
    {
        if (s_dir.Value is null)
        {
            Assert.Skip($"OCRia-nano models not installed; {OcriaNanoModels.InstallHint}.");
        }

        return s_dir.Value!;
    }

    public static string RepoFile(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine([dir ?? throw new InvalidOperationException("Repository root not found."), .. parts]);
    }
}
