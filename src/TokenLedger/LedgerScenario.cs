using System.Text;
using System.Text.Json;

namespace TokenLedger;

/// <summary>
/// One test flow whose model calls are replayed or recorded, measured, and compared with a committed baseline.
/// Open it with <see cref="Ledger.Scenario"/> and dispose it at the end of the test; disposing it calls <see cref="Verify"/>.
/// </summary>
/// <remarks>
/// Files, under the TokenLedger folder of the test project: <c>recordings/{name}.json</c> holds the model's
/// responses, <c>baselines/{name}.json</c> what the scenario costs and does. Commit both.
/// </remarks>
public sealed class LedgerScenario : IDisposable, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly LedgerOptions _settings;
    private readonly bool _update;
    private readonly string _project;
    private readonly LedgerScenario? _previous;
    private readonly List<LedgerCall> _calls = [];
    private readonly List<LedgerBudget> _budgets = [];
    private readonly List<string> _misses = [];
    private readonly Dictionary<string, Queue<RecordedInteraction>> _recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordedInteraction> _lastReplayed = new(StringComparer.Ordinal);
    private readonly List<RecordedInteraction> _used = [];
    private readonly int _recordedCount;
    private bool _newInteractions;
    private bool _verified;
    private List<LedgerDifference> _differences = [];

    internal LedgerScenario(string name, LedgerOptions settings, LedgerMode mode, bool update, string project, string root, LedgerScenario? previous)
    {
        Name = name;
        Mode = mode;
        FailOn = settings.FailOn;
        Tolerance = settings.Tolerance;
        _settings = settings;
        _update = update;
        _project = project;
        _previous = previous;
        var file = name.Replace('/', Path.DirectorySeparatorChar) + ".json";
        RecordingPath = Path.Combine(root, "recordings", file);
        BaselinePath = Path.Combine(root, "baselines", file);

        var pricingFile = Path.Combine(root, "pricing.json");
        Pricing = File.Exists(pricingFile) ? LedgerPricing.Load(pricingFile).MergedWith(settings.Pricing) : settings.Pricing;
        if (settings.DefaultBudget is { } budget) _budgets.Add(budget);

        if (mode is LedgerMode.Auto or LedgerMode.Replay && ReadRecording(RecordingPath) is { } recording)
        {
            foreach (var interaction in recording.Interactions)
            {
                if (!_recorded.TryGetValue(interaction.Fingerprint, out var queue)) _recorded[interaction.Fingerprint] = queue = new();
                queue.Enqueue(interaction);
                _recordedCount++;
            }
        }
    }

    /// <summary>The scenario's name, which is also the path of its files.</summary>
    public string Name { get; }

    /// <summary>The mode this scenario runs in.</summary>
    public LedgerMode Mode { get; }

    /// <summary>Which differences from the baseline fail this scenario. Default: <see cref="LedgerOptions.FailOn"/>.</summary>
    public LedgerFailOn FailOn { get; set; }

    /// <summary>Relative token change ignored for this scenario. Default: <see cref="LedgerOptions.Tolerance"/>.</summary>
    public double Tolerance
    {
        get;
        set => field = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The tolerance is a fraction between 0 and 1.");
    }

    /// <summary>The full path of the recording.</summary>
    public string RecordingPath { get; }

    /// <summary>The full path of the baseline.</summary>
    public string BaselinePath { get; }

    /// <summary>The prices in effect: <c>pricing.json</c> with <see cref="LedgerOptions.Pricing"/> on top.</summary>
    public LedgerPricing Pricing { get; }

    /// <summary>The model calls measured so far, in order.</summary>
    public IReadOnlyList<LedgerCall> Calls
    {
        get { lock (_lock) return [.. _calls]; }
    }

    /// <summary>The sums over <see cref="Calls"/>.</summary>
    public LedgerTotals Totals => LedgerTotals.Of(Calls);

    /// <summary>The estimated cost of <see cref="Calls"/> with <see cref="Pricing"/>.</summary>
    public LedgerCost Cost => LedgerCost.Of(Calls, Pricing);

    /// <summary>The differences from the baseline found by <see cref="Verify"/>; empty before it runs.</summary>
    public IReadOnlyList<LedgerDifference> Differences => _differences;

    /// <summary>Adds limits to this scenario; they are checked by <see cref="Verify"/>.</summary>
    public LedgerScenario Budget(int? maxCalls = null, long? maxInputTokens = null, long? maxOutputTokens = null, long? maxTokens = null, decimal? maxCost = null) =>
        Budget(new LedgerBudget { MaxCalls = maxCalls, MaxInputTokens = maxInputTokens, MaxOutputTokens = maxOutputTokens, MaxTokens = maxTokens, MaxCost = maxCost });

    /// <summary>Adds limits to this scenario; they are checked by <see cref="Verify"/>.</summary>
    public LedgerScenario Budget(LedgerBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        lock (_lock) _budgets.Add(budget);
        return this;
    }

    /// <summary>
    /// Closes the scenario: saves the recording, checks the budgets and compares the calls with the baseline,
    /// creating it when missing (outside CI) or rewriting it with <c>TOKENLEDGER_UPDATE=1</c>.
    /// Runs once; disposing the scenario calls it.
    /// </summary>
    /// <exception cref="TokenLedgerException">A request had no recording, a budget was exceeded, or the calls differ from the baseline.</exception>
    public void Verify()
    {
        if (_verified) return;
        _verified = true;
        Ledger.Restore(this, _previous);

        List<LedgerCall> calls;
        List<string> misses;
        lock (_lock)
        {
            calls = [.. _calls];
            misses = [.. _misses];
        }

        var failures = new List<string>();
        if (calls.Count == 0 && misses.Count == 0 && TokenLedgerChatClient.Created == 0)
        {
            failures.Add("No model call went through TokenLedger, and no TokenLedger client exists in this process. Add .UseTokenLedger() as the last step of the chat client pipeline, closest to the provider.");
        }

        SaveRecording();

        if (misses.Count > 0)
        {
            var report = new StringBuilder($"{misses.Count} model call(s) had no recording in {Display(RecordingPath)}:");
            foreach (var miss in misses) report.Append("\n  ").Append(miss);
            report.Append(LedgerEnvironment.IsCi
                ? "\nRecord them on your machine (TOKENLEDGER_MODE=record, or auto, the local default) and commit the recording."
                : "\nRun with TOKENLEDGER_MODE=record or auto to call the real model and record them. If the request contains variable data, normalize it: Ledger.Options.NormalizeTimestamps(), NormalizeGuids() or Normalize(...).");
            failures.Add(report.ToString());
        }

        failures.AddRange(CheckBudgets(calls));

        // With missing responses the measurements are incomplete: do not compare or rewrite the baseline.
        if (misses.Count == 0) failures.AddRange(CompareWithBaseline(calls));

        if (failures.Count > 0)
        {
            throw new TokenLedgerException($"TokenLedger: scenario '{Name}' failed.\n" + string.Join("\n", failures));
        }
    }

    /// <summary>Calls <see cref="Verify"/>.</summary>
    public void Dispose() => Verify();

    /// <summary>Calls <see cref="Verify"/>.</summary>
    public ValueTask DisposeAsync()
    {
        Verify();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Mode}): {Totals.Calls} calls, {Totals.Tokens} tokens";

    // Called by the middleware.

    internal LedgerOptions Settings => _settings;

    internal bool TryReplay(string fingerprint, out RecordedInteraction interaction)
    {
        lock (_lock)
        {
            if (_recorded.TryGetValue(fingerprint, out var queue) && queue.TryDequeue(out interaction!))
            {
                _lastReplayed[fingerprint] = interaction;
                _used.Add(interaction);
                return true;
            }

            // The same request again, more times than recorded: it gets the last recorded answer.
            if (_lastReplayed.TryGetValue(fingerprint, out interaction!)) return true;
            return false;
        }
    }

    internal void Miss(RecordedRequest request)
    {
        lock (_lock)
        {
            var index = _calls.Count + _misses.Count + 1;
            _misses.Add($"#{index} {request.Model ?? "(default model)"}, {request.Messages} message(s), last: {request.Last}");
        }
    }

    internal void Store(RecordedInteraction interaction)
    {
        lock (_lock)
        {
            _used.Add(interaction);
            _newInteractions = true;
        }
    }

    internal void Measure(string model, Microsoft.Extensions.AI.UsageDetails? usage, IReadOnlyList<string> tools, bool replayed, bool streaming)
    {
        lock (_lock)
        {
            _calls.Add(new LedgerCall
            {
                Index = _calls.Count + 1,
                Model = model,
                InputTokens = usage?.InputTokenCount,
                OutputTokens = usage?.OutputTokenCount,
                CachedInputTokens = usage?.CachedInputTokenCount,
                ReasoningTokens = usage?.ReasoningTokenCount,
                Tools = tools,
                Replayed = replayed,
                Streaming = streaming,
            });
        }
    }

    private void SaveRecording()
    {
        // Record rewrites the file with this run; Auto rewrites it when it learned something or some entries went unused.
        RecordingFile file;
        lock (_lock)
        {
            var rewrite = Mode switch
            {
                LedgerMode.Record => true,
                LedgerMode.Auto => _newInteractions || (_used.Count > 0 && _used.Count < _recordedCount),
                _ => false,
            };
            if (!rewrite || _used.Count == 0) return;   // a run without calls (a test that failed early) keeps the file
            file = new RecordingFile { Scenario = Name, Interactions = [.. _used] };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(RecordingPath)!);
        File.WriteAllText(RecordingPath, JsonSerializer.Serialize(file, LedgerJson.Indented.RecordingFile) + "\n");
    }

    private IEnumerable<string> CheckBudgets(List<LedgerCall> calls)
    {
        List<LedgerBudget> budgets;
        lock (_lock) budgets = [.. _budgets];
        if (budgets.Count == 0) yield break;

        var totals = LedgerTotals.Of(calls);
        var over = new List<string>();
        foreach (var budget in budgets)
        {
            if (budget.MaxCalls is { } maxCalls && totals.Calls > maxCalls) over.Add($"model calls {totals.Calls} > {maxCalls}");
            if (budget.MaxInputTokens is { } maxInput && totals.InputTokens > maxInput) over.Add($"input tokens {Baselines.Number(totals.InputTokens)} > {Baselines.Number(maxInput)}");
            if (budget.MaxOutputTokens is { } maxOutput && totals.OutputTokens > maxOutput) over.Add($"output tokens {Baselines.Number(totals.OutputTokens)} > {Baselines.Number(maxOutput)}");
            if (budget.MaxTokens is { } maxTokens && totals.Tokens > maxTokens) over.Add($"tokens {Baselines.Number(totals.Tokens)} > {Baselines.Number(maxTokens)}");
            if (budget.MaxCost is { } maxCost)
            {
                var cost = LedgerCost.Of(calls, Pricing);
                if (!cost.IsComplete)
                    over.Add($"cost cannot be checked: no price for {string.Join(", ", cost.UnpricedModels)}. Add it to pricing.json or Ledger.Options.Pricing");
                else if (cost.Amount > maxCost)
                    over.Add($"estimated cost {Baselines.Money(cost.Amount, cost.Currency)} > {Baselines.Money(maxCost, cost.Currency)}");
            }
        }

        if (totals.CallsWithoutUsage > 0 && budgets.Any(b => b.MaxInputTokens is not null || b.MaxOutputTokens is not null || b.MaxTokens is not null))
            over.Add($"{totals.CallsWithoutUsage} call(s) reported no token usage and are not counted");

        if (over.Count > 0) yield return "Over budget: " + string.Join("; ", over.Distinct()) + ".";
    }

    private IEnumerable<string> CompareWithBaseline(List<LedgerCall> calls)
    {
        var current = Baselines.Build(Name, calls);
        var baseline = Baselines.Read(BaselinePath);

        if (_update)
        {
            Baselines.Write(BaselinePath, current);
            yield break;
        }

        if (baseline is null)
        {
            if (LedgerEnvironment.IsCi)
            {
                yield return $"No baseline at {Display(BaselinePath)}. Run the tests on your machine to create it, and commit it.";
            }
            else
            {
                Baselines.Write(BaselinePath, current);
            }

            yield break;
        }

        _differences = Baselines.Compare(baseline, current, Tolerance);
        var failing = FailOn switch
        {
            LedgerFailOn.Change => _differences,
            LedgerFailOn.Regression => [.. _differences.Where(d => d.Kind == LedgerChangeKind.Regression)],
            _ => [],
        };
        if (failing.Count == 0) yield break;

        var report = new StringBuilder($"The calls differ from the baseline {Display(BaselinePath)}:");
        foreach (var difference in _differences) report.Append("\n  ").Append(difference);
        if (Baselines.Cost(baseline, Pricing) is { } before && Baselines.Cost(current, Pricing) is { } after && before != after)
        {
            report.Append("\n  estimated cost ").Append(Baselines.Money(before, Pricing.Currency)).Append(" → ").Append(Baselines.Money(after, Pricing.Currency)).Append(" per run");
        }

        report.Append("\nIf the change is intended, run the tests with TOKENLEDGER_UPDATE=1 and commit the baseline.");
        yield return report.ToString();
    }

    private string Display(string path) => LedgerPaths.Display(_project, path);

    private static RecordingFile? ReadRecording(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), LedgerJson.Lenient.RecordingFile);
        }
        catch (JsonException e)
        {
            throw new TokenLedgerException($"{path} is not a valid recording: {e.Message}. Delete it and record the scenario again.", e);
        }
    }
}
