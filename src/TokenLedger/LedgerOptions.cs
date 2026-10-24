using System.Text.RegularExpressions;

namespace TokenLedger;

/// <summary>What the middleware does with each model call inside a scenario.</summary>
public enum LedgerMode
{
    /// <summary>
    /// Replays recorded responses and calls the real model only for requests that have no recording,
    /// adding them to it. The default on a developer machine.
    /// </summary>
    Auto,

    /// <summary>Replays recorded responses only; a request without a recording fails. The default in CI.</summary>
    Replay,

    /// <summary>Calls the real model for every request and rewrites the scenario's recording.</summary>
    Record,

    /// <summary>Calls the real model and measures it, without reading or writing recordings.</summary>
    Live,
}

/// <summary>Which differences from the baseline fail a scenario.</summary>
public enum LedgerFailOn
{
    /// <summary>Any difference beyond the tolerance: regressions, behavior changes and improvements. The default.</summary>
    Change,

    /// <summary>Only regressions: more model calls, or more tokens beyond the tolerance.</summary>
    Regression,

    /// <summary>Nothing: differences are reported in the scenario but never fail it. Budgets still apply.</summary>
    Never,
}

/// <summary>Process-wide TokenLedger settings. Configure them once, before the first scenario.</summary>
public sealed class LedgerOptions
{
    private readonly List<Func<string, string>> _normalizers = [];
    private readonly List<Func<string, string>> _redactors = [];

    internal LedgerOptions() { }

    /// <summary>
    /// The folder for recordings, baselines and <c>pricing.json</c>. Relative paths are relative to the test project.
    /// Default: <c>tokenledger</c> in the test project. The <c>TOKENLEDGER_DIR</c> environment variable overrides it.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// The mode. When null: the <c>TOKENLEDGER_MODE</c> environment variable (auto, replay, record or live), then
    /// <see cref="LedgerMode.Replay"/> in CI, then <see cref="LedgerMode.Auto"/>. The environment variable wins over this property.
    /// </summary>
    public LedgerMode? Mode { get; set; }

    /// <summary>
    /// Accept the current measurements and rewrite the baselines instead of comparing.
    /// Also enabled by the <c>TOKENLEDGER_UPDATE=1</c> environment variable.
    /// </summary>
    public bool UpdateBaselines { get; set; }

    /// <summary>Which differences fail a scenario. Default: <see cref="LedgerFailOn.Change"/>.</summary>
    public LedgerFailOn FailOn { get; set; } = LedgerFailOn.Change;

    /// <summary>
    /// Relative change in input or output tokens that is ignored, from 0 to 1. Default: 0.10 (10 %).
    /// Changes in the number of calls, models or tools are never ignored.
    /// </summary>
    public double Tolerance
    {
        get;
        set => field = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The tolerance is a fraction between 0 and 1.");
    } = 0.10;

    /// <summary>
    /// Prices per million tokens. Entries added here win over <c>pricing.json</c> in <see cref="Directory"/>.
    /// TokenLedger ships no prices: without them, scenarios report calls and tokens only.
    /// </summary>
    public LedgerPricing Pricing { get; } = new();

    /// <summary>A budget applied to every scenario, in addition to the scenario's own.</summary>
    public LedgerBudget? DefaultBudget { get; set; }

    /// <summary>
    /// Rewrites text before a request is matched against the recordings, so variable data such as dates or ids
    /// does not prevent a replay. Applies to every string in the request: messages, instructions, tool arguments and results.
    /// </summary>
    public LedgerOptions Normalize(Func<string, string> normalizer)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        _normalizers.Add(normalizer);
        return this;
    }

    /// <summary>Replaces every match of <paramref name="pattern"/> before matching requests. See <see cref="Normalize(Func{string, string})"/>.</summary>
    public LedgerOptions Normalize(Regex pattern, string replacement)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Normalize(text => pattern.Replace(text, replacement));
    }

    /// <summary>Ignores GUIDs when matching requests.</summary>
    public LedgerOptions NormalizeGuids() => Normalize(Patterns.Guid(), "<guid>");

    /// <summary>Ignores ISO 8601 dates and timestamps (2026-10-24, 2026-10-24T07:47:00Z) when matching requests.</summary>
    public LedgerOptions NormalizeTimestamps() => Normalize(Patterns.Timestamp(), "<timestamp>");

    /// <summary>
    /// Rewrites text before it is written to a recording, to keep personal data and secrets out of the repository.
    /// Replays return the redacted text.
    /// </summary>
    public LedgerOptions Redact(Func<string, string> redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _redactors.Add(redactor);
        return this;
    }

    /// <summary>Replaces every match of <paramref name="pattern"/> in recorded text. See <see cref="Redact(Func{string, string})"/>.</summary>
    public LedgerOptions Redact(Regex pattern, string replacement)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Redact(text => pattern.Replace(text, replacement));
    }

    internal string ApplyNormalizers(string text)
    {
        foreach (var normalize in _normalizers) text = normalize(text);
        return text;
    }

    internal string ApplyRedactors(string text)
    {
        foreach (var redact in _redactors) text = redact(text);
        return text;
    }

    /// <summary>Back to the defaults; for TokenLedger's own tests.</summary>
    internal void Reset()
    {
        Directory = null;
        Mode = null;
        UpdateBaselines = false;
        FailOn = LedgerFailOn.Change;
        Tolerance = 0.10;
        DefaultBudget = null;
        Pricing.Clear();
        _normalizers.Clear();
        _redactors.Clear();
    }
}

internal static partial class Patterns
{
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    public static partial Regex Guid();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?\b")]
    public static partial Regex Timestamp();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)*$")]
    public static partial Regex ScenarioName();
}
