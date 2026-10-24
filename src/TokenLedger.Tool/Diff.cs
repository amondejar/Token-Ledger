namespace TokenLedger.Tool;

internal enum ScenarioStatus { Unchanged, Improvement, Change, Regression, Added, Removed }

/// <summary>One scenario compared between two sides.</summary>
internal sealed class ScenarioDiff
{
    public required string Folder { get; init; }
    public required string Scenario { get; init; }
    public BaselineFile? Before { get; init; }
    public BaselineFile? After { get; init; }
    public IReadOnlyList<LedgerDifference> Differences { get; init; } = [];
    public decimal? CostBefore { get; init; }
    public decimal? CostAfter { get; init; }
    public required string Currency { get; init; }
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];

    /// <summary>The worst difference wins.</summary>
    public ScenarioStatus Status =>
        Before is null ? ScenarioStatus.Added
        : After is null ? ScenarioStatus.Removed
        : Differences.Any(d => d.Kind == LedgerChangeKind.Regression) ? ScenarioStatus.Regression
        : Differences.Any(d => d.Kind == LedgerChangeKind.Change) ? ScenarioStatus.Change
        : Differences.Count > 0 ? ScenarioStatus.Improvement
        : ScenarioStatus.Unchanged;

    /// <summary>The scenario, prefixed with its project folder when the repository has several.</summary>
    public string DisplayName(bool withFolder) => withFolder ? $"{ProjectOf(Folder)}: {Scenario}" : Scenario;

    private static string ProjectOf(string folder)
    {
        var slash = folder.LastIndexOf('/');
        return slash < 0 ? "." : folder[..slash];
    }
}

/// <summary>The comparison of every scenario between two sides.</summary>
internal sealed class DiffResult
{
    public required string BaseLabel { get; init; }
    public required string HeadLabel { get; init; }
    public required IReadOnlyList<ScenarioDiff> Scenarios { get; init; }

    public bool SeveralFolders => Scenarios.Select(s => s.Folder).Distinct().Count() > 1;

    public int Count(ScenarioStatus status) => Scenarios.Count(s => s.Status == status);

    public int Differences(LedgerChangeKind kind) => Scenarios.Sum(s => s.Differences.Count(d => d.Kind == kind));

    public IEnumerable<ScenarioDiff> Changed => Scenarios.Where(s => s.Status != ScenarioStatus.Unchanged);

    /// <summary>Sum of the scenarios' cost per run on each side; null when some model has no price.</summary>
    public (decimal? Before, decimal? After, string Currency) TotalCost
    {
        get
        {
            var currency = Scenarios.Select(s => s.Currency).Distinct().ToList();
            if (currency.Count != 1) return (null, null, currency.FirstOrDefault() ?? "USD");
            decimal? before = 0, after = 0;
            foreach (var s in Scenarios)
            {
                if (s.Before is not null) before = s.CostBefore is { } b ? before + b : null;
                if (s.After is not null) after = s.CostAfter is { } a ? after + a : null;
            }

            return (before, after, currency[0]);
        }
    }

    public IReadOnlyList<string> UnpricedModels => [.. Scenarios.SelectMany(s => s.UnpricedModels).Distinct().Order(StringComparer.Ordinal)];

    public static DiffResult Compare(Source baseSide, Source headSide, double tolerance, string? pricingFile, string workingDirectory)
    {
        var before = baseSide.Baselines().ToDictionary(e => e.Path, StringComparer.Ordinal);
        var after = headSide.Baselines().ToDictionary(e => e.Path, StringComparer.Ordinal);
        var pricing = new Dictionary<string, LedgerPricing>(StringComparer.Ordinal);
        var explicitPricing = pricingFile is null ? null : LedgerPricing.Load(Path.GetFullPath(pricingFile, workingDirectory));

        // Prices of the day: the head side's pricing.json, else the base side's, for both sides.
        LedgerPricing PricingFor(string folder)
        {
            if (explicitPricing is not null) return explicitPricing;
            if (pricing.TryGetValue(folder, out var known)) return known;
            var path = folder + "/pricing.json";
            var json = headSide.Read(path) ?? baseSide.Read(path);
            return pricing[folder] = json is null ? new LedgerPricing() : ParsePricing(json, path);
        }

        var scenarios = new List<ScenarioDiff>();
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(path);
            var a = after.GetValueOrDefault(path);
            var entry = (a ?? b)!;
            var prices = PricingFor(entry.Folder);
            var baseline = b is null ? null : Parse(b);
            var head = a is null ? null : Parse(a);

            scenarios.Add(new ScenarioDiff
            {
                Folder = entry.Folder,
                Scenario = entry.Scenario,
                Before = baseline,
                After = head,
                Differences = baseline is not null && head is not null ? Baselines.Compare(baseline, head, tolerance) : [],
                CostBefore = baseline is null ? null : Baselines.Cost(baseline, prices),
                CostAfter = head is null ? null : Baselines.Cost(head, prices),
                Currency = prices.Currency,
                UnpricedModels = [.. new[] { baseline, head }.Where(f => f is not null).SelectMany(f => f!.Calls)
                    .Select(c => c.Model).Distinct().Where(m => prices.Find(m) is null)],
            });
        }

        return new DiffResult { BaseLabel = baseSide.Label, HeadLabel = headSide.Label, Scenarios = scenarios };
    }

    private static BaselineFile Parse(BaselineEntry entry)
    {
        try
        {
            return Baselines.Parse(entry.Content, entry.Path);
        }
        catch (TokenLedgerException e)
        {
            throw new ToolException(e.Message);
        }
    }

    private static LedgerPricing ParsePricing(string json, string path)
    {
        try
        {
            return LedgerPricing.Parse(json, path);
        }
        catch (TokenLedgerException e)
        {
            throw new ToolException(e.Message);
        }
    }
}
