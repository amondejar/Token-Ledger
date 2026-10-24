using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TokenLedger.Tool;

/// <summary>
/// Creates or completes <c>pricing.json</c> with an empty entry for every model the scenarios use, so the team only
/// fills in the prices. Existing entries are never changed.
/// </summary>
internal static partial class PricingInit
{
    private const string Header = "// Prices per million tokens, in the currency below. Fill in input and output; cachedInput is optional.\n" +
                                  "// Entries left empty are ignored. A model id also matches its dated snapshots (gpt-4.1 → gpt-4.1-2025-04-14).\n";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static int Run(IReadOnlyList<string> folders, string? currency, TextWriter output, string workingDirectory)
    {
        foreach (var folder in folders)
        {
            var models = ModelsUsed(folder);
            var file = Path.Combine(folder, "pricing.json");
            var display = Path.GetRelativePath(workingDirectory, file).Replace('\\', '/');

            JsonObject root;
            var existed = File.Exists(file);
            var text = existed ? File.ReadAllText(file) : null;
            try
            {
                root = text is null ? new JsonObject { ["currency"] = currency ?? "USD", ["models"] = new JsonObject() }
                    : JsonNode.Parse(text, documentOptions: Lenient) as JsonObject ?? throw new ToolException($"{display} is not a JSON object.");
            }
            catch (JsonException e)
            {
                throw new ToolException($"{display} is not valid JSON: {e.Message}");
            }

            if (root["models"] is not JsonObject entries)
            {
                if (root["models"] is not null) throw new ToolException($"{display}: \"models\" must be an object.");
                root["models"] = entries = new JsonObject();
            }

            var pricing = text is null ? new LedgerPricing() : LedgerPricing.Parse(text, display);
            var added = models
                .Where(m => pricing.Find(m) is null && !entries.Any(e => string.Equals(e.Key, m, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var model in added) entries[model] = new JsonObject { ["input"] = null, ["output"] = null };

            if (added.Count == 0 && existed)
            {
                output.WriteLine($"{display}: every model already has an entry ({models.Count} model(s)).");
                continue;
            }

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(file, Header + json + "\n");
            output.WriteLine(added.Count == 0
                ? $"{display}: created; no recorded models yet."
                : $"{display}: {(existed ? "added" : "created with")} {string.Join(", ", added)}. Fill in their prices per million tokens.");
            if (existed && text!.Contains("//", StringComparison.Ordinal)) output.WriteLine("  Comments in the file were replaced by the standard header.");
        }

        return Cli.Ok;
    }

    /// <summary>The TokenLedger folders under <paramref name="start"/>: folders named <paramref name="name"/> with baselines or recordings.</summary>
    public static IReadOnlyList<string> Find(string start, string name)
    {
        var found = new List<string>();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(start)]);
        while (pending.TryPop(out var directory))
        {
            if (directory.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (Directory.Exists(Path.Combine(directory.FullName, "baselines")) || Directory.Exists(Path.Combine(directory.FullName, "recordings"))))
            {
                found.Add(directory.FullName);
                continue;
            }

            IEnumerable<DirectoryInfo> children;
            try { children = directory.EnumerateDirectories(); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                if (child.Name is "bin" or "obj" or "node_modules" or "artifacts" || child.Name.StartsWith('.')) continue;
                pending.Push(child);
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>Model ids in the folder's baselines and recordings, without dated snapshot suffixes.</summary>
    internal static IReadOnlyList<string> ModelsUsed(string folder)
    {
        var models = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "baselines", "recordings" })
        {
            var directory = Path.Combine(folder, kind);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                JsonNode? json;
                try { json = JsonNode.Parse(File.ReadAllText(file), documentOptions: Lenient); }
                catch (JsonException) { continue; }

                var ids = kind == "baselines"
                    ? json?["calls"]?.AsArray().Select(c => c?["model"]?.GetValue<string>())
                    : json?["interactions"]?.AsArray().Select(i => i?["response"]?["model"]?.GetValue<string>());
                foreach (var id in ids ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(id) && id != "unknown") models.Add(WithoutSnapshot(id));
                }
            }
        }

        return [.. models];
    }

    internal static string WithoutSnapshot(string model) => Snapshot().Replace(model, "");

    [GeneratedRegex(@"(?:[-@:](?:\d{4}-\d{2}-\d{2}|\d{8}))$")]
    private static partial Regex Snapshot();
}
