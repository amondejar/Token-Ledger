namespace TokenLedger;

/// <summary>
/// Limits for one run of a scenario. Exceeding any of them fails the scenario.
/// A cost limit needs prices for every model the scenario uses.
/// </summary>
public sealed record LedgerBudget
{
    /// <summary>Maximum number of model calls.</summary>
    public int? MaxCalls { get; init; }

    /// <summary>Maximum input tokens, summed over every call.</summary>
    public long? MaxInputTokens { get; init; }

    /// <summary>Maximum output tokens, summed over every call.</summary>
    public long? MaxOutputTokens { get; init; }

    /// <summary>Maximum input plus output tokens, summed over every call.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Maximum estimated cost, in the pricing currency.</summary>
    public decimal? MaxCost { get; init; }
}
