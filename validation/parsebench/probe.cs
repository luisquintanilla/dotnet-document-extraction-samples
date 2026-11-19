#!/usr/bin/env dotnet
// How much of a document's text does each way of reading an ExtractionDocument see?
// Runs every configured provider over ten hard ParseBench-2 pages and reads each result five ways.
//
//   dotnet run probe.cs                      all configured providers
//   dotnet run probe.cs -- cu,docintel       only these providers
//
// Writes results/ next to this file: manifest.json, results.json, summary.md, and for each provider
// <doc>.document.json (the document's structure) and <doc>.<A-E>.txt (what each strategy read).
// Running some providers replaces only their results.
#:include ../../common/Providers.cs

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

string here = Here();
string outDir = Path.Combine(here, "results");
string[] providers = args.Length > 0 ? args[0].Split(',') : Providers.All;
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

var strategies = new (string Id, string Name, Func<ExtractionDocument, string> Read)[]
{
    ("A", "Two-level loop over text elements", TwoLevelLoop),
    ("B", "EnumerateContent() + text-element filter", EnumerateTextElements),
    ("C", "Recursive walker without image text", RecursiveWithoutImageText),
    ("D", "EnumerateContent() + GetText() per element", d => string.Join(Environment.NewLine, d.EnumerateContent().Select(e => e.GetText()).Where(t => t.Length > 0))),
    ("E", "document.GetText()", d => d.GetText()),
};

var corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(here, "corpus.json")))!;
List<(string Id, string Pdf, string Truth)> documents = await DownloadCorpusAsync(corpus, Path.Combine(here, ".corpus"));

// Rerunning some providers replaces only their results; the others are kept.
Directory.CreateDirectory(outDir);
string resultsPath = Path.Combine(outDir, "results.json");
string manifestPath = Path.Combine(outDir, "manifest.json");
var rows = File.Exists(resultsPath) ? JsonNode.Parse(File.ReadAllText(resultsPath))!.AsArray().Select(r => r!.AsObject().DeepClone().AsObject()).ToList() : [];
var manifest = File.Exists(manifestPath) ? JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject() : new JsonObject();
var providerRuns = manifest["providers"]?.DeepClone().AsObject() ?? new JsonObject();

foreach (string provider in providers)
{
    using var client = Providers.Create(provider, out string? skipped);
    if (client is null)
    {
        Console.WriteLine($"{provider,-9} skipped ({skipped})");
        continue;
    }

    rows.RemoveAll(r => (string)r["provider"]! == provider);
    providerRuns[provider] = ProviderRun(provider);
    string dumpDir = Path.Combine(outDir, provider);
    if (Directory.Exists(dumpDir)) Directory.Delete(dumpDir, recursive: true);
    Directory.CreateDirectory(dumpDir);
    foreach (var (docId, pdf, truthPath) in documents)
    {
        HashSet<string> truth = GroundTruthTokens(truthPath);
        var timer = Stopwatch.StartNew();
        ExtractionDocument document;
        try
        {
            document = await ExtractWithRetryAsync(client, pdf);
        }
        catch (Exception ex)
        {
            string error = Redact($"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"{provider,-9} {docId}: FAILED {Samples.Shorten(error, 160)}");
            rows.Add(new JsonObject { ["provider"] = provider, ["doc"] = docId, ["error"] = error });
            continue;
        }

        File.WriteAllText(Path.Combine(dumpDir, $"{docId}.document.json"), DocumentNode(document).ToJsonString(json));

        HashSet<string> fullTokens = Tokens(document.GetText());
        var results = new JsonObject();
        foreach (var (id, name, read) in strategies)
        {
            string text = read(document);
            HashSet<string> tokens = Tokens(text);
            results[id] = new JsonObject
            {
                ["strategy"] = name,
                ["chars"] = text.Length,
                ["truthRecall"] = truth.Count == 0 ? null : Ratio(tokens.Count(truth.Contains), truth.Count),
                ["shareOfGetText"] = Ratio(tokens.Count(fullTokens.Contains), fullTokens.Count),
                ["repeatedLines"] = RepeatedLines(text),
            };
            File.WriteAllText(Path.Combine(dumpDir, $"{docId}.{id}.txt"), text);
        }

        rows.Add(new JsonObject
        {
            ["provider"] = provider,
            ["doc"] = docId,
            ["seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1),
            ["truthTokens"] = truth.Count,
            ["shape"] = Shape(document),
            ["strategies"] = results,
        });
        Console.WriteLine($"{provider,-9} {docId,-60} recall A={results["A"]!["truthRecall"],-5} E={results["E"]!["truthRecall"],-5}");
    }
}

List<string> ran = [.. Providers.All.Where(p => providerRuns.ContainsKey(p))];
rows = [.. rows.OrderBy(r => ran.IndexOf((string)r["provider"]!)).ThenBy(r => (string)r["doc"]!, StringComparer.Ordinal)];
File.WriteAllText(resultsPath, new JsonArray([.. rows]).ToJsonString(json));
File.WriteAllText(manifestPath, Manifest(corpus, ran, providerRuns).ToJsonString(json));
File.WriteAllText(Path.Combine(outDir, "summary.md"), Summary(rows, ran, documents.Select(d => d.Id).ToList()));
Console.WriteLine($"Wrote {rows.Count} rows to {outDir}");

// Hosted OCR services rate-limit bursts; wait and retry rather than record a throttled page as a failure.
static async Task<ExtractionDocument> ExtractWithRetryAsync(DocumentExtractionClient client, string pdf)
{
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            return await Samples.ExtractFileAsync(client, pdf);
        }
        catch (HttpRequestException ex) when (attempt < 6 && (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests || ex.Message.Contains("429", StringComparison.Ordinal)))
        {
            var delay = TimeSpan.FromSeconds(15 * Math.Pow(2, attempt - 1));
            Console.WriteLine($"          throttled, retrying in {delay.TotalSeconds:0}s");
            await Task.Delay(delay);
        }
    }
}

static string TwoLevelLoop(ExtractionDocument document)
{
    var sb = new StringBuilder();
    foreach (var section in document.Sections)
    {
        foreach (var element in section.Elements)
        {
            if (element is ExtractionDocumentTextElement { Content: TextContent text })
            {
                sb.AppendLine(text.Text);
            }
        }
    }

    return sb.ToString();
}

static string EnumerateTextElements(ExtractionDocument document)
{
    var sb = new StringBuilder();
    foreach (var element in document.EnumerateContent())
    {
        if (element is ExtractionDocumentTextElement { Content: TextContent text })
        {
            sb.AppendLine(text.Text);
        }
    }

    return sb.ToString();
}

// The traversal the CommunityToolkit DocumentExtraction README teaches (PrintElement): it descends into
// sections and table cells and prints every TextContent, but doesn't read ExtractionDocumentImageElement.Text.
static string RecursiveWithoutImageText(ExtractionDocument document)
{
    var sb = new StringBuilder();
    foreach (var section in document.Sections) Visit(section);
    return sb.ToString();

    void Visit(ExtractionDocumentElement element)
    {
        if (element.Content is TextContent text) sb.AppendLine(text.Text);
        if (element is ExtractionDocumentSection section)
        {
            foreach (var child in section.Elements) Visit(child);
        }
        else if (element is ExtractionDocumentTableElement table)
        {
            foreach (var cell in table.Cells)
            {
                if (cell?.Content is { } content) Visit(content);
            }
        }
    }
}

// Structure only: kinds, roles, text, spans and page numbers. Metadata, raw provider responses,
// image bytes, URIs and bounding boxes are left out.
static JsonObject DocumentNode(ExtractionDocument document)
{
    var node = new JsonObject { ["identifier"] = Path.GetFileName(document.Identifier) };
    AddContent(node, document.Content);
    node["sections"] = new JsonArray([.. document.Sections.Select(s => (JsonNode)ElementNode(s))]);
    return node;
}

static JsonObject ElementNode(ExtractionDocumentElement element)
{
    var node = new JsonObject
    {
        ["kind"] = element switch
        {
            ExtractionDocumentSection => "section",
            ExtractionDocumentTextElement => "text",
            ExtractionDocumentTableElement => "table",
            ExtractionDocumentImageElement => "image",
            _ => element.GetType().Name,
        },
    };
    if (element is ExtractionDocumentTextElement { Role: { } role }) node["role"] = role;
    int[] pages = [.. element.Sources.Select(s => s.Page.PageNumber).OfType<int>().Distinct().Order()];
    if (pages.Length > 0) node["pages"] = new JsonArray([.. pages.Select(p => (JsonNode)p)]);
    AddContent(node, element.Content);

    switch (element)
    {
        case ExtractionDocumentImageElement image:
            if (image.Text is { } imageText) node["imageText"] = imageText;
            if (image.AlternativeText is { } alt) node["alternativeText"] = alt;
            break;

        case ExtractionDocumentTableElement table:
            node["rows"] = table.Cells.RowCount;
            node["columns"] = table.Cells.ColumnCount;
            var cells = new JsonArray();
            foreach (var cell in table.Cells)
            {
                if (cell is null) continue;
                var cellNode = new JsonObject { ["row"] = cell.RowIndex, ["column"] = cell.ColumnIndex };
                if (cell.RowSpan > 1) cellNode["rowSpan"] = cell.RowSpan;
                if (cell.ColumnSpan > 1) cellNode["columnSpan"] = cell.ColumnSpan;
                if (cell.Role is { } cellRole) cellNode["role"] = cellRole;
                if (cell.Content is { } content) cellNode["content"] = ElementNode(content);
                cells.Add(cellNode);
            }

            node["cells"] = cells;
            break;

        case ExtractionDocumentSection section:
            node["elements"] = new JsonArray([.. section.Elements.Select(e => (JsonNode)ElementNode(e))]);
            break;
    }

    return node;
}

static void AddContent(JsonObject node, AIContent? content)
{
    switch (content)
    {
        case null:
            break;
        case TextContent text:
            node["text"] = text.Text;
            break;
        case DataContent data:
            node["content"] = $"{data.MediaType}, {data.Data.Length} bytes";
            break;
        default:
            node["content"] = content.GetType().Name;
            break;
    }
}

static JsonObject Shape(ExtractionDocument document)
{
    int maxDepth = 0, nestedSections = 0, tables = 0, anchorCells = 0, mergedCells = 0, gridPositions = 0, sectionCells = 0, images = 0, imageText = 0;
    foreach (var section in document.Sections) Visit(section, 1);

    void Visit(ExtractionDocumentElement element, int depth)
    {
        switch (element)
        {
            case ExtractionDocumentSection section:
                maxDepth = Math.Max(maxDepth, depth);
                if (depth > 1) nestedSections++;
                foreach (var child in section.Elements) Visit(child, depth + 1);
                break;
            case ExtractionDocumentTableElement table:
                tables++;
                gridPositions += table.Cells.RowCount * table.Cells.ColumnCount;
                foreach (var cell in table.Cells)
                {
                    if (cell is null) continue;
                    anchorCells++;
                    if (cell.RowSpan > 1 || cell.ColumnSpan > 1) mergedCells++;
                    if (cell.Content is ExtractionDocumentSection) sectionCells++;
                    if (cell.Content is { } content) Visit(content, depth);
                }

                break;
            case ExtractionDocumentImageElement image:
                images++;
                if (!string.IsNullOrEmpty(image.Text)) imageText++;
                break;
        }
    }

    return new JsonObject
    {
        ["topSections"] = document.Sections.Count,
        ["maxDepth"] = maxDepth,
        ["nestedSections"] = nestedSections,
        ["tables"] = tables,
        ["gridPositions"] = gridPositions,
        ["anchorCells"] = anchorCells,
        ["mergedCells"] = mergedCells,
        ["sectionCells"] = sectionCells,
        ["images"] = images,
        ["imagesWithText"] = imageText,
    };
}

static JsonObject ProviderRun(string provider) => new()
{
    ["runUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),

    // Model and analyzer IDs only; endpoints and credentials are never recorded.
    ["settings"] = provider switch
    {
        "pdfpig" => "text layer, DocstrumBoundingBoxes",
        "ollama" => $"{Env("OLLAMA_VISION_MODEL") ?? "qwen3.5:4b"}, structured output {(Env("OLLAMA_STRUCTURED") is "0" or "false" ? "off" : "on, plain-markdown fallback")}",
        "mistral" => Env("AZURE_MISTRAL_OCR_URI") is not null ? $"Azure AI Foundry, {Env("AZURE_MISTRAL_OCR_MODEL") ?? "mistral-ocr-4-0"}" : $"Mistral API, {Env("MISTRAL_OCR_MODEL") ?? "mistral-ocr-latest"}",
        "docintel" => "prebuilt-layout",
        "cu" => Env("AZURE_CONTENT_UNDERSTANDING_ANALYZER") ?? "prebuilt-layout",
        _ => null,
    },
};

static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

static JsonObject Manifest(JsonNode corpus, List<string> ran, JsonObject providerRuns)
{
    string[] prefixes = ["Microsoft.Extensions.Documents", "Microsoft.Extensions.DocumentExtraction", "Microsoft.Extensions.AI", "CommunityToolkit.DocumentExtraction", "Azure.AI", "Azure.Core", "Azure.Identity", "OllamaSharp", "UglyToad.PdfPig", "PDFtoImage"];
    var packages = new JsonObject();
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
    {
        string name = assembly.GetName().Name!;
        if (prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
        {
            packages[name] = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString();
        }
    }

    var providers = new JsonObject();
    foreach (string provider in ran)
    {
        providers[provider] = providerRuns[provider]!.DeepClone();
    }

    return new JsonObject
    {
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["os"] = RuntimeInformation.OSDescription,
        ["dataset"] = corpus["dataset"]!.DeepClone(),
        ["datasetRevision"] = corpus["revision"]!.DeepClone(),
        ["providers"] = providers,
        ["packages"] = packages,
    };
}

static string Summary(List<JsonObject> rows, List<string> ran, List<string> docs)
{
    string[] ids = ["A", "B", "C", "D", "E"];
    var sb = new StringBuilder();
    sb.AppendLine("# Results");
    sb.AppendLine();
    sb.AppendLine("Mean recall of each page's ground-truth tokens. Compare columns within a row: the numbers measure how");
    sb.AppendLine("much of what a provider returned each reading strategy sees, not how good the providers are.");
    sb.AppendLine();
    sb.AppendLine("| Provider | Pages | Nested pages | A | B | C | D | E |");
    sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (string provider in ran)
    {
        var ok = rows.Where(r => (string)r["provider"]! == provider && r["strategies"] is not null).ToList();
        if (ok.Count == 0) continue;
        int nested = ok.Count(r => (int)r["shape"]!["maxDepth"]! > 1);
        var means = ids.Select(id => ok.Select(r => (double?)r["strategies"]![id]!["truthRecall"]).OfType<double>().DefaultIfEmpty().Average().ToString("0.00"));
        sb.AppendLine($"| {Providers.DisplayNames.GetValueOrDefault(provider, provider)} | {ok.Count}/{docs.Count} | {nested} | {string.Join(" | ", means)} |");
    }

    sb.AppendLine();
    sb.AppendLine("A: two-level loop over text elements. B: `EnumerateContent()` keeping only text elements. C: recursive walker that");
    sb.AppendLine("skips image text. D: `EnumerateContent()` + `GetText()` per element. E: `document.GetText()`.");

    var failures = rows.Where(r => r["error"] is not null).ToList();
    if (failures.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("## Failures");
        sb.AppendLine();
        foreach (var r in failures) sb.AppendLine($"- {r["provider"]} on `{r["doc"]}`: {Samples.Shorten((string)r["error"]!, 200)}");
    }

    sb.AppendLine();
    sb.AppendLine("## Per page (recall A → E)");
    sb.AppendLine();
    sb.AppendLine($"| Page | {string.Join(" | ", ran)} |");
    sb.AppendLine($"|---|{string.Concat(ran.Select(_ => "---|"))}");
    foreach (string doc in docs)
    {
        var cells = ran.Select(p => rows.FirstOrDefault(r => (string)r["provider"]! == p && (string)r["doc"]! == doc) switch
        {
            null => "",
            { } r when r["error"] is not null => "failed",
            { } r => $"{r["strategies"]!["A"]!["truthRecall"]} → {r["strategies"]!["E"]!["truthRecall"]}",
        });
        sb.AppendLine($"| `{doc}` | {string.Join(" | ", cells)} |");
    }

    return sb.ToString();
}

static async Task<List<(string Id, string Pdf, string Truth)>> DownloadCorpusAsync(JsonNode corpus, string cacheDir)
{
    Directory.CreateDirectory(cacheDir);
    using var http = new HttpClient();
    string baseUrl = $"https://huggingface.co/datasets/{corpus["dataset"]}/resolve/{corpus["revision"]}/";
    var documents = new List<(string, string, string)>();
    foreach (var doc in corpus["documents"]!.AsArray())
    {
        string id = (string)doc!["id"]!;
        string remote = string.Join('/', ((string)doc["path"]!).Split('/').Select(Uri.EscapeDataString));
        string pdf = await FetchAsync($"{remote}.pdf", Path.Combine(cacheDir, $"{id}.pdf"), (string)doc["pdfSha256"]!);
        string truth = await FetchAsync($"{remote}.test.json", Path.Combine(cacheDir, $"{id}.test.json"), (string)doc["testSha256"]!);
        documents.Add((id, pdf, truth));
    }

    return documents;

    async Task<string> FetchAsync(string remote, string local, string sha256)
    {
        if (!File.Exists(local) || Sha256(local) != sha256)
        {
            Console.WriteLine($"Downloading {Uri.UnescapeDataString(remote)}");
            await File.WriteAllBytesAsync(local, await http.GetByteArrayAsync(baseUrl + remote));
            if (Sha256(local) != sha256) throw new InvalidDataException($"{local} doesn't match the SHA-256 in corpus.json.");
        }

        return local;
    }

    static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}

static HashSet<string> GroundTruthTokens(string path)
{
    var tokens = new HashSet<string>();
    var root = JsonNode.Parse(File.ReadAllText(path))!;
    if (root["expected_markdown"]?.GetValue<string>() is { } markdown) tokens.UnionWith(Tokens(markdown));
    if (root["test_rules"] is JsonArray rules) Collect(rules, null);
    return tokens;

    void Collect(JsonNode? node, string? key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (k, v) in obj) Collect(v, k);
                break;
            case JsonArray array:
                foreach (var item in array) Collect(item, key);
                break;
            case JsonValue value when value.TryGetValue(out string? s) && key is not ("type" or "id" or "tags" or "rule_id" or "page"):
                tokens.UnionWith(Tokens(s));
                break;
        }
    }
}

// Lowercased words and numbers; CJK text counts one token per ideograph.
static HashSet<string> Tokens(string text)
{
    text = Regex.Replace(text, "<[^>]+>", " ");
    return Regex.Matches(text.ToLowerInvariant(), @"\p{IsCJKUnifiedIdeographs}|[\p{L}\p{N}-[\p{IsCJKUnifiedIdeographs}]]+")
        .Select(m => m.Value)
        .Where(t => t.Length >= 2 || char.IsDigit(t[0]) || t[0] >= '\u4e00')
        .ToHashSet();
}

static double Ratio(int part, int whole) => whole == 0 ? 1 : Math.Round((double)part / whole, 3);

static int RepeatedLines(string text) => text.Split('\n')
    .Select(l => l.Trim())
    .Where(l => l.Length >= 25)
    .GroupBy(l => l)
    .Sum(g => g.Count() - 1);

static string Redact(string text) => Regex.Replace(text, @"https?://[^\s'""]+", "<url>");

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
