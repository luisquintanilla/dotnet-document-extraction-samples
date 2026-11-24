#!/usr/bin/env dotnet
// How close is the C# OCRia port to LiteParse itself?
// reference/ holds LiteParse's Markdown for the repo's books and the ParseBench probe pages, made by
// .github/workflows/liteparse-reference.yml. This runs the C# client on the same inputs and compares
// words: the share of LiteParse's words the port finds, the share of the port's words LiteParse has,
// and how much of the word order agrees. For ParseBench pages it also scores both against the truth.
//
//   dotnet run compare.cs        (run validation/parsebench/probe.cs once first, for the corpus)
//
// Writes results/<input>.txt (the port's text) and results/summary.md.
#:include ../../common/Providers.cs

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Documents;

string here = Here();
string repo = Path.GetFullPath(Path.Combine(here, "..", ".."));
string corpusDir = Path.Combine(repo, "validation", "parsebench", ".corpus");
string outDir = Path.Combine(here, "results");
Directory.CreateDirectory(outDir);

using var client = Providers.Create("ocria", out string? skipped) ?? throw new InvalidOperationException($"ocria: {skipped}");

var rows = new List<string>();
var totals = new List<(double Recall, double Precision, double Order)>();
foreach (string reference in Directory.GetFiles(Path.Combine(here, "reference"), "*.md").Order(StringComparer.Ordinal))
{
    string name = Path.GetFileNameWithoutExtension(reference);
    string? input = Input(name);
    if (input is null || !File.Exists(input))
    {
        Console.WriteLine($"{name}: input not found, skipped");
        continue;
    }

    ExtractionDocument document = await Samples.ExtractFileAsync(client, input);
    string ours = document.GetText();
    string theirText = File.ReadAllText(reference);
    File.WriteAllText(Path.Combine(outDir, $"{name}.txt"), ours);

    HashSet<string> theirs = Tokens(theirText), mine = Tokens(ours);
    double recall = Ratio(theirs.Count(mine.Contains), theirs.Count);
    double precision = Ratio(mine.Count(theirs.Contains), mine.Count);
    List<string> theirWords = Words(theirText), ourWords = Words(ours);
    double order = Ratio(2 * Lcs(theirWords, ourWords), theirWords.Count + ourWords.Count);
    totals.Add((recall, precision, order));

    string truthCells = " | |";
    if (name.StartsWith("parsebench__", StringComparison.Ordinal))
    {
        HashSet<string> truth = GroundTruthTokens(Path.Combine(corpusDir, $"{name["parsebench__".Length..]}.test.json"));
        HashSet<string> theirsRaw = Tokens(theirText, keepImageLinks: true), mineRaw = Tokens(ours, keepImageLinks: true);
        truthCells = $" {Ratio(truth.Count(theirsRaw.Contains), truth.Count):0.000} | {Ratio(truth.Count(mineRaw.Contains), truth.Count):0.000} |";
    }

    rows.Add($"| {name} | {recall:0.000} | {precision:0.000} | {order:0.000} |{truthCells}");
    Console.WriteLine(rows[^1]);
}

var md = new StringBuilder();
md.AppendLine("# C# port vs LiteParse");
md.AppendLine();
md.AppendLine($"LiteParse ({File.ReadAllText(Path.Combine(here, "reference", "lit-version.txt")).Trim()}) with `--ocr-backend ocria-nano --format markdown`, against `OcriaNanoDocumentExtractionClient` `GetText()` with default options.");
md.AppendLine("Words are lowercased runs of letters and digits (CJK: one per ideograph); Markdown image links and HTML tags are dropped.");
md.AppendLine();
md.AppendLine("- **Recall**: share of LiteParse's distinct words the port also produced.");
md.AppendLine("- **Precision**: share of the port's distinct words LiteParse also produced.");
md.AppendLine("- **Order**: 2·LCS / (LiteParse words + port words) over the word sequences; 1.0 when every word appears in the same order.");
md.AppendLine("- **Truth**: ParseBench ground-truth token recall (the probe's measure), for LiteParse and for the port.");
md.AppendLine();
md.AppendLine("| Input | Recall | Precision | Order | Truth (LiteParse) | Truth (port) |");
md.AppendLine("|---|---|---|---|---|---|");
rows.ForEach(r => md.AppendLine(r));
if (totals.Count > 0)
{
    md.AppendLine($"| **Mean** | **{totals.Average(t => t.Recall):0.000}** | **{totals.Average(t => t.Precision):0.000}** | **{totals.Average(t => t.Order):0.000}** | | |");
}

File.WriteAllText(Path.Combine(outDir, "summary.md"), md.ToString());
Console.WriteLine($"Wrote {Path.Combine(outDir, "summary.md")}");

string? Input(string name)
{
    string[] parts = name.Split("__", 2);
    if (parts.Length != 2) return null;
    return parts[0] switch
    {
        "parsebench" => Path.Combine(corpusDir, $"{parts[1]}.pdf"),
        "books" when parts[1].EndsWith(".png", StringComparison.Ordinal) => Path.Combine(repo, "books", "pages", parts[1]),
        "books" => new[] { "pdf", "pages" }.Select(d => Path.Combine(repo, "books", d, $"{parts[1]}.pdf")).FirstOrDefault(File.Exists),
        _ => null,
    };
}

static List<string> Words(string text, bool keepImageLinks = false)
{
    text = Regex.Replace(text, keepImageLinks ? "<[^>]+>" : @"!\[[^\]]*\]\([^)]*\)|<[^>]+>", " ");
    return [.. Regex.Matches(text.ToLowerInvariant(), @"\p{IsCJKUnifiedIdeographs}|[\p{L}\p{N}-[\p{IsCJKUnifiedIdeographs}]]+").Select(m => m.Value)];
}

// The probe's token set: words of two or more characters, numbers, and ideographs.
// keepImageLinks matches the probe exactly, which the ground truth is scored with.
static HashSet<string> Tokens(string text, bool keepImageLinks = false) => Words(text, keepImageLinks)
    .Where(t => t.Length >= 2 || char.IsDigit(t[0]) || t[0] >= '\u4e00')
    .ToHashSet();

static int Lcs(List<string> a, List<string> b)
{
    var prev = new int[b.Count + 1];
    var cur = new int[b.Count + 1];
    for (int i = 1; i <= a.Count; i++)
    {
        for (int j = 1; j <= b.Count; j++)
        {
            cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
        }

        (prev, cur) = (cur, prev);
    }

    return prev[b.Count];
}

// The probe's ground truth: expected_markdown plus every string in test_rules.
static HashSet<string> GroundTruthTokens(string path)
{
    var tokens = new HashSet<string>();
    var root = JsonNode.Parse(File.ReadAllText(path))!;
    if (root["expected_markdown"]?.GetValue<string>() is { } markdown) tokens.UnionWith(Tokens(markdown, keepImageLinks: true));
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
                tokens.UnionWith(Tokens(s, keepImageLinks: true));
                break;
        }
    }
}

static double Ratio(int part, int whole) => whole == 0 ? 1 : Math.Round((double)part / whole, 3);

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
