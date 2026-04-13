using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string DefaultModel = "gemini-1.5-pro";
var defaultExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".go", ".rs", ".cs", ".cpp",
    ".c", ".h", ".hpp", ".php", ".rb", ".swift", ".kt", ".scala", ".sql"
};

var options = ParseArgs(args);
var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set GEMINI_API_KEY environment variable before running.");
    return 1;
}

var repoPath = Path.GetFullPath(options.RepoPath);
if (!Directory.Exists(repoPath))
{
    Console.Error.WriteLine($"Repository path does not exist: {repoPath}");
    return 1;
}

var snippets = ReadCodeFiles(repoPath, defaultExtensions, options.MaxFiles, options.MaxCharsPerFile);
if (snippets.Count == 0)
{
    Console.Error.WriteLine("No code files found in repository.");
    return 1;
}

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

var selectedPaths = await PlannerPhaseAsync(
    httpClient,
    apiKey,
    options.Model,
    snippets,
    Math.Min(options.TopFiles, snippets.Count));

if (selectedPaths.Count == 0)
{
    selectedPaths = snippets.Take(Math.Min(8, snippets.Count)).Select(s => s.Path).ToList();
}

var snippetByPath = snippets.ToDictionary(s => s.Path, StringComparer.Ordinal);
var reviews = new JsonArray();
foreach (var path in selectedPaths)
{
    if (!snippetByPath.TryGetValue(path, out var snippet))
    {
        continue;
    }

    var review = await ReviewerPhaseAsync(httpClient, apiKey, options.Model, snippet);
    reviews.Add(review);
}

var judged = await JudgePhaseAsync(httpClient, apiKey, options.Model, reviews);
var markdown = RenderMarkdown(judged);
var outputPath = Path.GetFullPath(options.OutputPath);
await File.WriteAllTextAsync(outputPath, markdown, Encoding.UTF8);
Console.WriteLine($"Wrote review report: {outputPath}");
return 0;

static List<FileSnippet> ReadCodeFiles(
    string root,
    HashSet<string> allowedExtensions,
    int maxFiles,
    int maxCharsPerFile)
{
    var result = new List<FileSnippet>();
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var extension = Path.GetExtension(file);
        if (!allowedExtensions.Contains(extension))
        {
            continue;
        }

        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Any(part => part.StartsWith('.', StringComparison.Ordinal)))
        {
            continue;
        }

        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch
        {
            continue;
        }

        var numbered = NumberLines(content);
        if (numbered.Length > maxCharsPerFile)
        {
            numbered = numbered[..maxCharsPerFile];
        }

        var relativePath = Path.GetRelativePath(root, file);
        result.Add(new FileSnippet(relativePath, numbered));
        if (result.Count >= maxFiles)
        {
            break;
        }
    }

    return result;
}

static string NumberLines(string content)
{
    var lines = content.Replace("\r\n", "\n").Split('\n');
    var builder = new StringBuilder();
    for (var i = 0; i < lines.Length; i++)
    {
        builder.Append($"{i + 1,4}: ");
        builder.Append(lines[i]);
        if (i < lines.Length - 1)
        {
            builder.Append('\n');
        }
    }

    return builder.ToString();
}

static async Task<string> GeminiGenerateAsync(
    HttpClient httpClient,
    string apiKey,
    string model,
    string prompt,
    double temperature = 0.2)
{
    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
    var payload = new JsonObject
    {
        ["contents"] = new JsonArray(
            new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt })
            }),
        ["generationConfig"] = new JsonObject { ["temperature"] = temperature }
    };

    using var response = await httpClient.PostAsync(
        url,
        new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));

    var body = await response.Content.ReadAsStringAsync();
    response.EnsureSuccessStatusCode();

    JsonNode? root;
    try
    {
        root = JsonNode.Parse(body);
    }
    catch (JsonException ex)
    {
        throw new InvalidOperationException($"Unable to parse Gemini response JSON: {ex.Message}\n{body}");
    }

    var candidates = root?["candidates"]?.AsArray();
    if (candidates is null || candidates.Count == 0)
    {
        throw new InvalidOperationException($"No candidates returned from Gemini: {body}");
    }

    var parts = candidates[0]?["content"]?["parts"]?.AsArray();
    if (parts is null)
    {
        throw new InvalidOperationException($"No content parts returned from Gemini: {body}");
    }

    var text = new StringBuilder();
    foreach (var part in parts)
    {
        var fragment = part?["text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(fragment))
        {
            text.Append(fragment);
        }
    }

    if (text.Length == 0)
    {
        throw new InvalidOperationException($"Gemini returned empty text: {body}");
    }

    return text.ToString();
}

static JsonObject? ExtractJsonObject(string raw)
{
    var text = raw.Trim();
    if (text.StartsWith("```", StringComparison.Ordinal))
    {
        text = text.Trim('`');
        if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].Trim();
        }
    }

    if (TryParseObject(text, out var parsed))
    {
        return parsed;
    }

    var start = text.IndexOf('{');
    var end = text.LastIndexOf('}');
    if (start >= 0 && end > start)
    {
        var segment = text[start..(end + 1)];
        if (TryParseObject(segment, out parsed))
        {
            return parsed;
        }
    }

    return null;
}

static bool TryParseObject(string text, out JsonObject? obj)
{
    obj = null;
    try
    {
        var node = JsonNode.Parse(text);
        obj = node as JsonObject;
        return obj is not null;
    }
    catch (JsonException)
    {
        return false;
    }
}

static async Task<List<string>> PlannerPhaseAsync(
    HttpClient client,
    string apiKey,
    string model,
    List<FileSnippet> snippets,
    int topN)
{
    var inventory = new JsonArray();
    foreach (var s in snippets)
    {
        inventory.Add(new JsonObject
        {
            ["path"] = s.Path,
            ["approx_lines"] = s.Content.Split('\n').Length
        });
    }

    var prompt = $"""
You are a code review planning agent.
Pick the {topN} files that are most likely to contain impactful defects.

Rules:
- Focus on correctness, security, data loss, race conditions, and API misuse.
- Return ONLY valid JSON with this schema:
  {{
    "selected_files": ["path1", "path2"]
  }}

Repository file inventory:
{inventory.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}
""";

    var response = await GeminiGenerateAsync(client, apiKey, model, prompt, 0.1);
    var parsed = ExtractJsonObject(response);
    var selected = parsed?["selected_files"]?.AsArray();
    if (selected is null)
    {
        return [];
    }

    var selectedSet = new HashSet<string>(selected
        .Select(n => n?.GetValue<string>())
        .Where(v => !string.IsNullOrWhiteSpace(v))!
        .Cast<string>(), StringComparer.Ordinal);

    return snippets
        .Where(s => selectedSet.Contains(s.Path))
        .Take(topN)
        .Select(s => s.Path)
        .ToList();
}

static async Task<JsonObject> ReviewerPhaseAsync(
    HttpClient client,
    string apiKey,
    string model,
    FileSnippet snippet)
{
    var prompt = $"""
You are a senior static code reviewer.
Review this single file and report only high-value findings.

Return ONLY valid JSON:
{{
  "file": "{snippet.Path}",
  "findings": [
    {{
      "severity": "critical|high|medium|low",
      "line": 123,
      "title": "short issue title",
      "impact": "why this matters",
      "evidence": "what in code indicates this",
      "fix": "specific remediation"
    }}
  ]
}}

If no issues are found, return empty findings.

File content with line numbers:
{snippet.Content}
""";

    var response = await GeminiGenerateAsync(client, apiKey, model, prompt, 0.2);
    var parsed = ExtractJsonObject(response) ?? new JsonObject();
    parsed["file"] ??= snippet.Path;
    parsed["findings"] ??= new JsonArray();
    return parsed;
}

static async Task<JsonObject> JudgePhaseAsync(
    HttpClient client,
    string apiKey,
    string model,
    JsonArray reviews)
{
    var prompt = $"""
You are a code review judge agent.
Deduplicate and calibrate severity. Keep only credible findings.

Return ONLY valid JSON:
{{
  "summary": "1-3 sentence summary",
  "final_findings": [
    {{
      "severity": "critical|high|medium|low",
      "file": "path",
      "line": 123,
      "title": "issue",
      "impact": "risk",
      "fix": "remediation"
    }}
  ]
}}

Input reviews:
{reviews.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}
""";

    var response = await GeminiGenerateAsync(client, apiKey, model, prompt, 0.1);
    var parsed = ExtractJsonObject(response);
    if (parsed is null)
    {
        return new JsonObject
        {
            ["summary"] = "Unable to parse judge output.",
            ["final_findings"] = new JsonArray()
        };
    }

    parsed["summary"] ??= "No summary generated.";
    parsed["final_findings"] ??= new JsonArray();
    return parsed;
}

static string RenderMarkdown(JsonObject result)
{
    var builder = new StringBuilder();
    builder.AppendLine("# Agentic Gemini Code Review Report");
    builder.AppendLine();
    builder.AppendLine(result["summary"]?.GetValue<string>() ?? "No summary available.");
    builder.AppendLine();
    builder.AppendLine("## Findings");
    builder.AppendLine();

    var findings = result["final_findings"] as JsonArray;
    if (findings is null || findings.Count == 0)
    {
        builder.AppendLine("- No actionable findings detected.");
        return builder.ToString();
    }

    var severityOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = 0,
        ["high"] = 1,
        ["medium"] = 2,
        ["low"] = 3
    };

    var sorted = findings
        .OfType<JsonObject>()
        .OrderBy(f => severityOrder.GetValueOrDefault(f["severity"]?.GetValue<string>() ?? string.Empty, 9))
        .ToList();

    for (var i = 0; i < sorted.Count; i++)
    {
        var finding = sorted[i];
        var severity = (finding["severity"]?.GetValue<string>() ?? "unknown").ToUpperInvariant();
        var file = finding["file"]?.GetValue<string>() ?? "unknown";
        var line = finding["line"]?.ToString() ?? "?";
        var title = finding["title"]?.GetValue<string>() ?? "Untitled issue";
        var impact = finding["impact"]?.GetValue<string>() ?? "No impact provided.";
        var fix = finding["fix"]?.GetValue<string>() ?? "No remediation provided.";

        builder.AppendLine($"### {i + 1}. [{severity}] {title}");
        builder.AppendLine($"- Location: `{file}:{line}`");
        builder.AppendLine($"- Impact: {impact}");
        builder.AppendLine($"- Fix: {fix}");
        builder.AppendLine();
    }

    return builder.ToString();
}

static Options ParseArgs(string[] args)
{
    var options = new Options();
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = arg[2..];
        string? value = null;
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[++i];
        }

        switch (key)
        {
            case "repo":
                options.RepoPath = value ?? options.RepoPath;
                break;
            case "model":
                options.Model = value ?? options.Model;
                break;
            case "max-files":
                if (int.TryParse(value, out var maxFiles) && maxFiles > 0)
                {
                    options.MaxFiles = maxFiles;
                }
                break;
            case "top-files":
                if (int.TryParse(value, out var topFiles) && topFiles > 0)
                {
                    options.TopFiles = topFiles;
                }
                break;
            case "max-chars-per-file":
                if (int.TryParse(value, out var maxChars) && maxChars > 0)
                {
                    options.MaxCharsPerFile = maxChars;
                }
                break;
            case "output":
                options.OutputPath = value ?? options.OutputPath;
                break;
        }
    }

    return options;
}

record FileSnippet(string Path, string Content);

sealed class Options
{
    public string RepoPath { get; set; } = ".";
    public string Model { get; set; } = DefaultModel;
    public int MaxFiles { get; set; } = 40;
    public int TopFiles { get; set; } = 12;
    public int MaxCharsPerFile { get; set; } = 16000;
    public string OutputPath { get; set; } = "review-report.md";
}
