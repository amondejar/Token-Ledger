namespace TokenLedger;

/// <summary>One model call measured inside a scenario.</summary>
public sealed class LedgerCall
{
    /// <summary>Position of the call in the scenario, from 1.</summary>
    public required int Index { get; init; }

    /// <summary>The model that answered: the response's model id, else the request's, else the client's default.</summary>
    public required string Model { get; init; }

    /// <summary>Input tokens reported by the provider, cached ones included; null when it reported none.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Output tokens reported by the provider, reasoning ones included; null when it reported none.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Input tokens read from the provider's cache (part of <see cref="InputTokens"/>).</summary>
    public long? CachedInputTokens { get; init; }

    /// <summary>Reasoning tokens (part of <see cref="OutputTokens"/>).</summary>
    public long? ReasoningTokens { get; init; }

    /// <summary>The tools the model asked to call in its response, in order.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>True when the response came from the recording; false when the real model answered.</summary>
    public bool Replayed { get; init; }

    /// <summary>
    /// The HTTP endpoint that made the call, such as <c>POST api/support/chat</c>, when TokenLedger.AspNetCore
    /// attributes it; null otherwise.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>True for a streaming call.</summary>
    public bool Streaming { get; init; }

    /// <summary>False when the provider reported no token usage for this call.</summary>
    public bool UsageReported => InputTokens is not null || OutputTokens is not null;

    /// <inheritdoc />
    public override string ToString() =>
        $"#{Index} {Model}: {(UsageReported ? $"{InputTokens ?? 0} in, {OutputTokens ?? 0} out" : "no usage reported")}"
        + (Tools.Count > 0 ? $", tools {string.Join(", ", Tools)}" : "") + (Endpoint is null ? "" : $", from {Endpoint}") + (Replayed ? " (replayed)" : "");
}

/// <summary>The sums over a scenario's calls.</summary>
/// <param name="Calls">Number of model calls.</param>
/// <param name="InputTokens">Input tokens, cached ones included.</param>
/// <param name="OutputTokens">Output tokens, reasoning ones included.</param>
/// <param name="CachedInputTokens">Input tokens read from the provider's cache.</param>
/// <param name="ReasoningTokens">Reasoning tokens.</param>
/// <param name="CallsWithoutUsage">Calls whose provider reported no usage; their tokens are not in the sums.</param>
public sealed record LedgerTotals(int Calls, long InputTokens, long OutputTokens, long CachedInputTokens, long ReasoningTokens, int CallsWithoutUsage)
{
    /// <summary>Input plus output tokens.</summary>
    public long Tokens => InputTokens + OutputTokens;

    internal static LedgerTotals Of(IEnumerable<LedgerCall> calls)
    {
        int count = 0, unreported = 0;
        long input = 0, output = 0, cached = 0, reasoning = 0;
        foreach (var call in calls)
        {
            count++;
            if (!call.UsageReported) unreported++;
            input += call.InputTokens ?? 0;
            output += call.OutputTokens ?? 0;
            cached += call.CachedInputTokens ?? 0;
            reasoning += call.ReasoningTokens ?? 0;
        }

        return new LedgerTotals(count, input, output, cached, reasoning, unreported);
    }
}

/// <summary>The estimated cost of a scenario run.</summary>
/// <param name="Amount">The cost of the calls whose model has a price.</param>
/// <param name="Currency">The pricing currency.</param>
/// <param name="UnpricedModels">Models without a price; their calls are not in <paramref name="Amount"/>.</param>
public sealed record LedgerCost(decimal Amount, string Currency, IReadOnlyList<string> UnpricedModels)
{
    /// <summary>True when every call had a price.</summary>
    public bool IsComplete => UnpricedModels.Count == 0;

    internal static LedgerCost Of(IEnumerable<LedgerCall> calls, LedgerPricing pricing)
    {
        var amount = 0m;
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (!call.UsageReported) continue;
            if (pricing.Cost(call.Model, call.InputTokens ?? 0, call.OutputTokens ?? 0, call.CachedInputTokens ?? 0) is { } cost) amount += cost;
            else unpriced.Add(call.Model);
        }

        return new LedgerCost(amount, pricing.Currency, [.. unpriced]);
    }
}
