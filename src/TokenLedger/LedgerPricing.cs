using System.Text.Json;

namespace TokenLedger;

/// <summary>A model's prices per million tokens, in the currency of its <see cref="LedgerPricing"/>.</summary>
/// <param name="Input">Price of a million input tokens.</param>
/// <param name="Output">Price of a million output tokens (reasoning tokens included).</param>
/// <param name="CachedInput">Price of a million input tokens read from the provider's cache; null charges them as input.</param>
public sealed record ModelPrice(decimal Input, decimal Output, decimal? CachedInput = null);

/// <summary>
/// Your prices, per million tokens. TokenLedger ships none: prices depend on your contract, region and currency,
/// and change too often for a package. Load them from <c>pricing.json</c> or add them in code.
/// </summary>
/// <remarks>
/// A model id matches an entry with the same name, or one followed by a dated snapshot suffix
/// (<c>gpt-4.1</c> prices <c>gpt-4.1-2025-04-14</c>, not <c>gpt-4.1-mini</c>). An entry ending in <c>*</c> matches any model that starts with it.
/// </remarks>
public sealed class LedgerPricing
{
    private readonly Dictionary<string, ModelPrice> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The currency of every price, for reports. Default: USD.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>The prices, by model id or pattern.</summary>
    public IReadOnlyDictionary<string, ModelPrice> Models => _models;

    /// <summary>Adds or replaces a model's prices per million tokens.</summary>
    public LedgerPricing Add(string model, decimal input, decimal output, decimal? cachedInput = null) =>
        Add(model, new ModelPrice(input, output, cachedInput));

    /// <summary>Adds or replaces a model's prices per million tokens.</summary>
    public LedgerPricing Add(string model, ModelPrice price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(price);
        if (price.Input < 0 || price.Output < 0 || price.CachedInput < 0) throw new ArgumentOutOfRangeException(nameof(price), "Prices cannot be negative.");
        _models[model.Trim()] = price;
        return this;
    }

    /// <summary>The prices for a model id, or null when no entry matches it.</summary>
    public ModelPrice? Find(string model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        if (_models.TryGetValue(model, out var exact)) return exact;

        ModelPrice? best = null;
        var bestLength = -1;
        foreach (var (key, price) in _models)
        {
            var matches = key.EndsWith('*')
                ? model.StartsWith(key[..^1], StringComparison.OrdinalIgnoreCase)
                : model.Length > key.Length + 1
                    && model.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                    && model[key.Length] is '-' or '@' or ':'
                    && char.IsAsciiDigit(model[key.Length + 1]);
            if (matches && key.Length > bestLength) (best, bestLength) = (price, key.Length);
        }

        return best;
    }

    /// <summary>The cost of a call, or null when the model has no price.</summary>
    public decimal? Cost(string model, long inputTokens, long outputTokens, long cachedInputTokens = 0)
    {
        if (Find(model) is not { } price) return null;
        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        return ((inputTokens - cached) * price.Input + cached * (price.CachedInput ?? price.Input) + outputTokens * price.Output) / 1_000_000m;
    }

    /// <summary>
    /// Reads prices from a JSON file: <c>{ "currency": "EUR", "models": { "gpt-4.1": { "input": 1.85, "output": 7.40, "cachedInput": 0.46 } } }</c>.
    /// Comments and trailing commas are allowed.
    /// </summary>
    public static LedgerPricing Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PricingFile? file;
        try
        {
            using var stream = File.OpenRead(path);
            file = JsonSerializer.Deserialize(stream, LedgerJson.Lenient.PricingFile);
        }
        catch (JsonException e)
        {
            throw new TokenLedgerException($"{path} is not a valid pricing file: {e.Message}", e);
        }

        var pricing = new LedgerPricing();
        if (file is null) return pricing;
        if (!string.IsNullOrWhiteSpace(file.Currency)) pricing.Currency = file.Currency.Trim();
        foreach (var (model, price) in file.Models ?? [])
        {
            if (price.Input is not { } input || price.Output is not { } output) continue;   // left empty to fill in later
            pricing.Add(model, input, output, price.CachedInput);
        }

        return pricing;
    }

    internal void Clear()
    {
        _models.Clear();
        Currency = "USD";
    }

    /// <summary>This pricing with the entries of <paramref name="overrides"/> on top.</summary>
    internal LedgerPricing MergedWith(LedgerPricing overrides)
    {
        var merged = new LedgerPricing { Currency = overrides._models.Count > 0 || _models.Count == 0 ? overrides.Currency : Currency };
        foreach (var (model, price) in _models) merged._models[model] = price;
        foreach (var (model, price) in overrides._models) merged._models[model] = price;
        return merged;
    }
}
