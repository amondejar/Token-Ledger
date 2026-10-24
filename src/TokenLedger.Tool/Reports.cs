using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TokenLedger.Tool;

/// <summary>Renders a <see cref="DiffResult"/> as text, Markdown or JSON.</summary>
internal static class Reports
{
    public const string Marker = "<!-- tokenledger-diff -->";

    public static string Text(DiffResult result)
    {
        var text = new StringBuilder();
        text.Append($"TokenLedger: {result.BaseLabel} → {result.HeadLabel}\n");
        if (result.Scenarios.Count == 0) return text.Append("No baselines found.\n").ToString();
        if (!result.Changed.Any()) return text.Append($"No changes in {Plural(result.Scenarios.Count, "scenario")}.\n").Append(Footer(result, markdown: false)).ToString();

        text.Append(Counts(result)).Append('\n');
        foreach (var s in result.Changed)
        {
            text.Append('\n').Append(s.DisplayName(result.SeveralFolders)).Append("  ").Append(Label(s.Status)).Append('\n');
            switch (s.Status)
            {
                case ScenarioStatus.Added:
                    text.Append("  ").Append(Describe(s.After!, s.CostAfter, s.Currency)).Append('\n');
                    break;
                case ScenarioStatus.Removed:
                    text.Append("  was ").Append(Describe(s.Before!, s.CostBefore, s.Currency)).Append('\n');
                    break;
                default:
                    foreach (var d in s.Differences) text.Append("  ").Append(d).Append('\n');
                    if (CostChange(s) is { } cost) text.Append("  estimated cost ").Append(cost).Append(" per run\n");
                    break;
            }
        }

        return text.Append(Footer(result, markdown: false)).ToString();
    }

    public static string Markdown(DiffResult result)
    {
        var md = new StringBuilder();
        md.Append("### TokenLedger\n\n");
        if (result.Scenarios.Count == 0) return md.Append("No baselines found.\n").ToString();

        md.Append(Counts(result, markdown: true)).Append($" · `{result.BaseLabel}` → `{result.HeadLabel}`\n\n");
        if (!result.Changed.Any())
        {
            md.Append($"No changes in {Plural(result.Scenarios.Count, "scenario")}.\n");
            return md.Append(Footer(result, markdown: true)).ToString();
        }

        md.Append("| Scenario | Model calls | Tokens | Cost per run | |\n");
        md.Append("| --- | ---: | ---: | ---: | --- |\n");
        foreach (var s in result.Changed)
        {
            md.Append($"| `{s.DisplayName(result.SeveralFolders)}` | {Pair(s.Before?.Calls.Count, s.After?.Calls.Count, n => n.ToString(CultureInfo.InvariantCulture))} " +
                      $"| {Pair(Tokens(s.Before), Tokens(s.After), Baselines.Number)} | {CostCell(s)} | {Icon(s.Status)} {Label(s.Status)} |\n");
        }

        foreach (var s in result.Changed.Where(s => s.Differences.Count > 0))
        {
            md.Append($"\n<details><summary><code>{Escape(s.DisplayName(result.SeveralFolders))}</code></summary>\n\n");
            foreach (var d in s.Differences)
                md.Append($"- {Icon(d.Kind)} **{d.Kind.ToString().ToLowerInvariant()}**: {Escape(d.Description)}\n");
            md.Append("\n</details>\n");
        }

        return md.Append(Footer(result, markdown: true)).ToString();
    }

    public static string Json(DiffResult result)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("base", result.BaseLabel);
            w.WriteString("head", result.HeadLabel);
            w.WriteStartObject("summary");
            w.WriteNumber("scenarios", result.Scenarios.Count);
            foreach (var status in Enum.GetValues<ScenarioStatus>()) w.WriteNumber(Camel(status.ToString()), result.Count(status));
            w.WriteNumber("regressions", result.Differences(LedgerChangeKind.Regression));
            w.WriteNumber("changes", result.Differences(LedgerChangeKind.Change));
            w.WriteNumber("improvements", result.Differences(LedgerChangeKind.Improvement));
            w.WriteEndObject();

            var (costBefore, costAfter, currency) = result.TotalCost;
            w.WriteStartObject("cost");
            w.WriteString("currency", currency);
            Number(w, "before", costBefore);
            Number(w, "after", costAfter);
            w.WriteStartArray("unpricedModels");
            foreach (var m in result.UnpricedModels) w.WriteStringValue(m);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("scenarios");
            foreach (var s in result.Scenarios)
            {
                w.WriteStartObject();
                w.WriteString("folder", s.Folder);
                w.WriteString("scenario", s.Scenario);
                w.WriteString("status", Camel(s.Status.ToString()));
                Side(w, "before", s.Before, s.CostBefore);
                Side(w, "after", s.After, s.CostAfter);
                w.WriteStartArray("differences");
                foreach (var d in s.Differences)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", Camel(d.Kind.ToString()));
                    w.WriteString("description", d.Description);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>What every scenario costs today.</summary>
    public static string Summary(DiffResult result, bool markdown)
    {
        var report = new StringBuilder();
        if (result.Scenarios.Count == 0) return "No baselines found.\n";
        var several = result.SeveralFolders;
        var rows = result.Scenarios.Select(s => (
            Name: s.DisplayName(several),
            Calls: s.After!.Calls.Count.ToString(CultureInfo.InvariantCulture),
            Tokens: Baselines.Number(Tokens(s.After) ?? 0),
            Cost: s.CostAfter is { } c ? Baselines.Money(c, s.Currency) : "no price")).ToList();

        if (markdown)
        {
            report.Append("### TokenLedger\n\n| Scenario | Model calls | Tokens | Cost per run |\n| --- | ---: | ---: | ---: |\n");
            foreach (var r in rows) report.Append($"| `{r.Name}` | {r.Calls} | {r.Tokens} | {r.Cost} |\n");
        }
        else
        {
            var width = Math.Max(8, rows.Max(r => r.Name.Length));
            report.Append($"{"Scenario".PadRight(width)}  {"Calls",6}  {"Tokens",10}  {"Cost per run",16}\n");
            foreach (var r in rows) report.Append($"{r.Name.PadRight(width)}  {r.Calls,6}  {r.Tokens,10}  {r.Cost,16}\n");
        }

        var (_, total, currency) = result.TotalCost;
        var calls = result.Scenarios.Sum(s => s.After!.Calls.Count);
        var tokens = result.Scenarios.Sum(s => Tokens(s.After) ?? 0);
        report.Append(markdown ? "\n" : "\n").Append($"{Plural(result.Scenarios.Count, "scenario")}, {Plural(calls, "model call")}, {Baselines.Number(tokens)} tokens");
        report.Append(total is { } t ? $", {Baselines.Money(t, currency)} per run of the suite.\n" : ".\n");
        if (result.UnpricedModels.Count > 0) report.Append(Unpriced(result.UnpricedModels, markdown));
        return report.ToString();
    }

    private static string Counts(DiffResult result, bool markdown = false)
    {
        var parts = new (ScenarioStatus Status, string Name)[]
        {
            (ScenarioStatus.Regression, "regression"), (ScenarioStatus.Change, "change"), (ScenarioStatus.Improvement, "improvement"),
            (ScenarioStatus.Added, "new"), (ScenarioStatus.Removed, "removed"), (ScenarioStatus.Unchanged, "unchanged"),
        }.Select(p =>
        {
            var count = result.Count(p.Status);
            var noun = p.Status is ScenarioStatus.Regression or ScenarioStatus.Change or ScenarioStatus.Improvement && count != 1 ? p.Name + "s" : p.Name;
            var part = $"{count} {noun}";
            return markdown && p.Status == ScenarioStatus.Regression && count > 0 ? $"**{part}**" : part;
        });
        return string.Join(markdown ? " · " : ", ", parts);
    }

    private static string Footer(DiffResult result, bool markdown)
    {
        var footer = new StringBuilder();
        var (before, after, currency) = result.TotalCost;
        if (before is { } b && after is { } a && b != a)
        {
            var change = b == 0 ? "" : $" ({(a > b ? "+" : "")}{((a - b) / b * 100).ToString("0", CultureInfo.InvariantCulture)} %)";
            footer.Append(markdown ? "\n**All scenarios, estimated cost per run:** " : "\nAll scenarios, estimated cost per run: ")
                .Append($"{Baselines.Money(b, currency)} → {Baselines.Money(a, currency)}{change}\n");
        }

        if (result.UnpricedModels.Count > 0) footer.Append('\n').Append(Unpriced(result.UnpricedModels, markdown));
        return footer.ToString();
    }

    private static string Unpriced(IReadOnlyList<string> models, bool markdown) => markdown
        ? $"<sub>No price for {string.Join(", ", models.Select(m => $"`{m}`"))}: costs leave them out. Add them to `pricing.json` (`dotnet tokenledger pricing init`).</sub>\n"
        : $"No price for {string.Join(", ", models)}: costs leave them out. Add them to pricing.json (dotnet tokenledger pricing init).\n";

    private static string Describe(BaselineFile baseline, decimal? cost, string currency) =>
        $"{Plural(baseline.Calls.Count, "model call")}, {Baselines.Number(baseline.Totals.Input + baseline.Totals.Output)} tokens"
        + (cost is { } c ? $", {Baselines.Money(c, currency)} per run" : "");

    private static string? CostChange(ScenarioDiff s) =>
        s.CostBefore is { } b && s.CostAfter is { } a && b != a ? $"{Baselines.Money(b, s.Currency)} → {Baselines.Money(a, s.Currency)}" : null;

    private static string CostCell(ScenarioDiff s) =>
        (s.CostBefore, s.CostAfter) switch
        {
            ({ } b, { } a) when b == a => Baselines.Money(a, s.Currency),
            ({ } b, { } a) => $"{Baselines.Money(b, s.Currency)} → {Baselines.Money(a, s.Currency)}",
            (null, { } a) when s.Before is null => Baselines.Money(a, s.Currency),
            ({ } b, null) when s.After is null => Baselines.Money(b, s.Currency),
            _ => "no price",
        };

    private static long? Tokens(BaselineFile? baseline) => baseline is null ? null : baseline.Totals.Input + baseline.Totals.Output;

    private static string Pair<T>(T? before, T? after, Func<T, string> format) where T : struct, IEquatable<T> =>
        (before, after) switch
        {
            ({ } b, { } a) when b.Equals(a) => format(a),
            ({ } b, { } a) => $"{format(b)} → {format(a)}",
            (null, { } a) => format(a),
            ({ } b, null) => format(b),
            _ => "",
        };

    private static string Label(ScenarioStatus status) => status switch
    {
        ScenarioStatus.Added => "new",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string Icon(ScenarioStatus status) => status switch
    {
        ScenarioStatus.Regression => "🔴",
        ScenarioStatus.Change => "🟡",
        ScenarioStatus.Improvement => "🟢",
        ScenarioStatus.Added => "🆕",
        ScenarioStatus.Removed => "➖",
        _ => "",
    };

    private static string Icon(LedgerChangeKind kind) => kind switch
    {
        LedgerChangeKind.Regression => "🔴",
        LedgerChangeKind.Change => "🟡",
        _ => "🟢",
    };

    private static string Escape(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "\\|");

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static void Number(Utf8JsonWriter w, string name, decimal? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static void Side(Utf8JsonWriter w, string name, BaselineFile? baseline, decimal? cost)
    {
        if (baseline is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteNumber("calls", baseline.Calls.Count);
        w.WriteNumber("input", baseline.Totals.Input);
        w.WriteNumber("output", baseline.Totals.Output);
        w.WriteNumber("cachedInput", baseline.Totals.CachedInput);
        Number(w, "cost", cost);
        w.WriteStartArray("tools");
        foreach (var tool in baseline.Calls.SelectMany(c => c.Tools)) w.WriteStringValue(tool);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
