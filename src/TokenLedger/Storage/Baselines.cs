using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TokenLedger;

/// <summary>Builds, reads, writes and compares baselines: what a scenario costs and does, without the response text.</summary>
internal static class Baselines
{
    public static BaselineFile Build(string scenario, IReadOnlyList<LedgerCall> calls)
    {
        var totals = LedgerTotals.Of(calls);
        return new BaselineFile
        {
            Scenario = scenario,
            Calls = [.. calls.Select(c => new BaselineCall
            {
                Model = c.Model,
                Input = c.InputTokens,
                Output = c.OutputTokens,
                CachedInput = c.CachedInputTokens is > 0 ? c.CachedInputTokens : null,
                Reasoning = c.ReasoningTokens is > 0 ? c.ReasoningTokens : null,
                Tools = [.. c.Tools],
            })],
            Totals = new BaselineTotals
            {
                Calls = totals.Calls,
                Input = totals.InputTokens,
                Output = totals.OutputTokens,
                CachedInput = totals.CachedInputTokens,
                Reasoning = totals.ReasoningTokens,
                Unreported = totals.CallsWithoutUsage > 0 ? totals.CallsWithoutUsage : null,
            },
        };
    }

    public static BaselineFile? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), LedgerJson.Lenient.BaselineFile)
                ?? throw new TokenLedgerException($"{path} is empty.");
        }
        catch (JsonException e)
        {
            throw new TokenLedgerException($"{path} is not a valid baseline: {e.Message}. Fix it, or delete it and run the tests to create it again.", e);
        }
    }

    /// <summary>Indented, with one call per line, so a pull request diff shows each call that changed.</summary>
    public static string Serialize(BaselineFile baseline)
    {
        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"format\": ").Append(baseline.Format.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        json.Append("  \"scenario\": ").Append(JsonSerializer.Serialize(baseline.Scenario, LedgerJson.Compact.String)).Append(",\n");
        json.Append("  \"calls\": [");
        for (var i = 0; i < baseline.Calls.Count; i++)
        {
            json.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(JsonSerializer.Serialize(baseline.Calls[i], LedgerJson.Compact.BaselineCall));
        }

        json.Append(baseline.Calls.Count > 0 ? "\n  ],\n" : "],\n");
        json.Append("  \"totals\": ").Append(JsonSerializer.Serialize(baseline.Totals, LedgerJson.Compact.BaselineTotals)).Append('\n');
        json.Append("}\n");
        return json.ToString();
    }

    public static void Write(string path, BaselineFile baseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(baseline));
    }

    public static List<LedgerDifference> Compare(BaselineFile before, BaselineFile after, double tolerance)
    {
        var differences = new List<LedgerDifference>();

        var (callsBefore, callsAfter) = (before.Calls.Count, after.Calls.Count);
        if (callsAfter != callsBefore)
        {
            differences.Add(new(callsAfter > callsBefore ? LedgerChangeKind.Regression : LedgerChangeKind.Improvement, $"model calls {callsBefore} → {callsAfter}"));
        }

        Tokens(differences, "input tokens", before.Totals.Input, after.Totals.Input, tolerance);
        Tokens(differences, "output tokens", before.Totals.Output, after.Totals.Output, tolerance);

        var models = (Sequence(before.Calls.Select(c => c.Model).Distinct()), Sequence(after.Calls.Select(c => c.Model).Distinct()));
        if (models.Item1 != models.Item2) differences.Add(new(LedgerChangeKind.Change, $"models: {models.Item1} → {models.Item2}"));

        var tools = (Sequence(before.Calls.SelectMany(c => c.Tools)), Sequence(after.Calls.SelectMany(c => c.Tools)));
        if (tools.Item1 != tools.Item2) differences.Add(new(LedgerChangeKind.Change, $"tools: {tools.Item1} → {tools.Item2}"));

        var (unreportedBefore, unreportedAfter) = (before.Totals.Unreported ?? 0, after.Totals.Unreported ?? 0);
        if (unreportedBefore != unreportedAfter)
            differences.Add(new(LedgerChangeKind.Change, $"calls without usage data {unreportedBefore} → {unreportedAfter}"));

        return differences;
    }

    /// <summary>The estimated cost of a baseline's calls, or null when a model has no price.</summary>
    public static decimal? Cost(BaselineFile baseline, LedgerPricing pricing)
    {
        var total = 0m;
        foreach (var call in baseline.Calls)
        {
            if (call.Input is null && call.Output is null) continue;
            if (pricing.Cost(call.Model, call.Input ?? 0, call.Output ?? 0, call.CachedInput ?? 0) is not { } cost) return null;
            total += cost;
        }

        return total;
    }

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Money(decimal amount, string currency) =>
        $"{amount.ToString(amount != 0 && Math.Abs(amount) < 0.01m ? "0.######" : "0.00##", CultureInfo.InvariantCulture)} {currency}";

    private static void Tokens(List<LedgerDifference> differences, string what, long before, long after, double tolerance)
    {
        if (before == after) return;
        var relative = before == 0 ? double.PositiveInfinity : (after - before) / (double)before;
        if (Math.Abs(relative) <= tolerance) return;

        var percent = double.IsInfinity(relative) ? "" : $" ({(relative > 0 ? "+" : "")}{(relative * 100).ToString("0", CultureInfo.InvariantCulture)} %)";
        differences.Add(new(after > before ? LedgerChangeKind.Regression : LedgerChangeKind.Improvement, $"{what} {Number(before)} → {Number(after)}{percent}"));
    }

    private static string Sequence(IEnumerable<string> items)
    {
        var list = string.Join(", ", items);
        return list.Length == 0 ? "none" : list;
    }
}
